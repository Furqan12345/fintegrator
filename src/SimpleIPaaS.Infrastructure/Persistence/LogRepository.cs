using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Reads are done through EF; that is safe because DatabaseLoggerProvider suppresses the
// "Microsoft.EntityFrameworkCore.*" categories, so the SQL EF logs for these queries can
// never be written back into AppLogEntries.
public class LogRepository : ILogRepository
{
    // Ordered by severity so a "minimum level" filter can be expanded into a set of names.
    private static readonly LogLevel[] SeverityOrder =
    {
        LogLevel.Trace,
        LogLevel.Debug,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error,
        LogLevel.Critical
    };

    public static IReadOnlyList<string> LevelNames { get; } =
        SeverityOrder.Select(level => level.ToString()).ToArray();

    private readonly IPaaSContext _context;

    public LogRepository(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<LogPage> GetPageAsync(LogQuery query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 50 : (pageSize > 500 ? 500 : pageSize);

        var filtered = Apply(_context.AppLogEntries.AsNoTracking(), query);

        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .OrderByDescending(entry => entry.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // The cursor is the newest id in the whole table (not just this page) so a client
        // can page through history and still start tailing from "now" without gaps.
        var cursor = await GetMaxIdAsync(cancellationToken);

        return new LogPage(items, page, pageSize, total, cursor);
    }

    public async Task<LogTail> GetTailAsync(long afterId, LogQuery query, int limit, CancellationToken cancellationToken = default)
    {
        limit = limit < 1 ? 200 : (limit > 1000 ? 1000 : limit);

        var maxId = await GetMaxIdAsync(cancellationToken);

        // A cursor ahead of the table means the client is holding a stale cursor from
        // before a "clear logs" (AUTOINCREMENT ids never repeat, but the table can be
        // emptied). Serve the newest window instead of silently returning nothing forever.
        var reset = afterId > maxId;
        if (reset)
        {
            afterId = 0;
        }

        var filtered = Apply(_context.AppLogEntries.AsNoTracking(), query)
            .Where(entry => entry.Id > afterId);

        List<AppLogEntry> entries;
        if (reset)
        {
            entries = await filtered
                .OrderByDescending(entry => entry.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);
            entries.Reverse();
        }
        else
        {
            entries = await filtered
                .OrderBy(entry => entry.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }

        // Advance only as far as we actually delivered, so a capped batch is not skipped.
        var cursor = entries.Count > 0 ? entries[^1].Id : (reset ? maxId : afterId);

        return new LogTail(entries, cursor, reset);
    }

    public async Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.AppLogEntries
            .AsNoTracking()
            .Select(entry => entry.Source)
            .Distinct()
            .OrderBy(source => source)
            .ToListAsync(cancellationToken);
    }

    public async Task<LogFacets> GetFacetsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = Apply(_context.AppLogEntries.AsNoTracking(), query);
        var services = await filtered.Where(item => item.Service != "").Select(item => item.Service).Distinct().OrderBy(item => item).Take(100).ToListAsync(cancellationToken);
        var components = await filtered.Where(item => item.Component != "").Select(item => item.Component).Distinct().OrderBy(item => item).Take(200).ToListAsync(cancellationToken);
        var events = await filtered.Where(item => item.EventName != "").Select(item => item.EventName).Distinct().OrderBy(item => item).Take(300).ToListAsync(cancellationToken);
        var outcomes = await filtered.Where(item => item.Outcome != "").Select(item => item.Outcome).Distinct().OrderBy(item => item).Take(50).ToListAsync(cancellationToken);
        var cardTypes = await filtered.Where(item => item.CardType != null && item.CardType != "").Select(item => item.CardType!).Distinct().OrderBy(item => item).Take(100).ToListAsync(cancellationToken);
        return new LogFacets(services, components, events, outcomes, cardTypes);
    }

    public async Task<IReadOnlyList<AppLogEntry>> GetExecutionTimelineAsync(Guid executionId, int limit, CancellationToken cancellationToken = default)
    {
        limit = limit < 1 ? 500 : Math.Min(limit, 5000);
        return await _context.AppLogEntries.AsNoTracking()
            .Where(entry => entry.FlowExecutionId == executionId)
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(entry => entry.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<TelemetryHealthSnapshot> GetTelemetryHealthAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _context.AppLogEntries.AsNoTracking().OrderByDescending(entry => entry.Id).Select(entry => (DateTime?)entry.Timestamp).FirstOrDefaultAsync(cancellationToken);
        var total = await _context.AppLogEntries.AsNoTracking().LongCountAsync(cancellationToken);
        var recentCutoff = DateTime.UtcNow.AddMinutes(-15);
        var errors = await _context.AppLogEntries.AsNoTracking().LongCountAsync(entry => entry.Timestamp >= recentCutoff && (entry.Level == "Error" || entry.Level == "Critical"), cancellationToken);
        return new TelemetryHealthSnapshot(latest, total, errors);
    }
    public async Task<int> DeleteAsync(DateTime? olderThanUtc, CancellationToken cancellationToken = default)
    {
        var target = _context.AppLogEntries.AsQueryable();
        if (olderThanUtc.HasValue)
        {
            target = target.Where(entry => entry.Timestamp < olderThanUtc.Value);
        }

        return await target.ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<long> GetMaxIdAsync(CancellationToken cancellationToken)
    {
        return await _context.AppLogEntries
            .AsNoTracking()
            .Select(entry => (long?)entry.Id)
            .MaxAsync(cancellationToken) ?? 0L;
    }

    private static IQueryable<AppLogEntry> Apply(IQueryable<AppLogEntry> source, LogQuery query)
    {
        var allowedLevels = ExpandMinimumLevel(query.MinimumLevel);
        if (allowedLevels != null)
        {
            source = source.Where(entry => allowedLevels.Contains(entry.Level));
        }

        if (!string.IsNullOrWhiteSpace(query.Service)) source = source.Where(entry => entry.Service == query.Service.Trim());
        if (!string.IsNullOrWhiteSpace(query.Component)) source = source.Where(entry => entry.Component == query.Component.Trim());
        if (!string.IsNullOrWhiteSpace(query.EventName)) source = source.Where(entry => entry.EventName == query.EventName.Trim());
        if (!string.IsNullOrWhiteSpace(query.Operation)) source = source.Where(entry => entry.Operation == query.Operation.Trim());
        if (!string.IsNullOrWhiteSpace(query.Outcome)) source = source.Where(entry => entry.Outcome == query.Outcome.Trim());
        if (query.CardId.HasValue) source = source.Where(entry => entry.CardId == query.CardId.Value);
        if (query.StepExecutionId.HasValue) source = source.Where(entry => entry.StepExecutionId == query.StepExecutionId.Value);
        if (!string.IsNullOrWhiteSpace(query.TraceId)) source = source.Where(entry => entry.TraceId == query.TraceId);
        if (query.Invocation.HasValue) source = source.Where(entry => entry.Invocation == query.Invocation.Value);
        if (query.RetryAttempt.HasValue) source = source.Where(entry => entry.RetryAttempt == query.RetryAttempt.Value);
        if (query.IsTest.HasValue) source = source.Where(entry => entry.IsTest == query.IsTest.Value);
        if (query.TestCaseId.HasValue) source = source.Where(entry => entry.TestCaseId == query.TestCaseId.Value);
        if (query.TestRunId.HasValue) source = source.Where(entry => entry.TestRunId == query.TestRunId.Value);

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            var wanted = query.Source.Trim();
            source = source.Where(entry => entry.Source == wanted);
        }

        if (query.IntegrationId.HasValue)
        {
            source = source.Where(entry => entry.IntegrationId == query.IntegrationId.Value);
        }

        if (query.FlowExecutionId.HasValue)
        {
            source = source.Where(entry => entry.FlowExecutionId == query.FlowExecutionId.Value);
        }

        if (query.FlowId.HasValue)
        {
            source = source.Where(entry => entry.FlowId == query.FlowId.Value);
        }

        if (query.SinceUtc.HasValue)
        {
            source = source.Where(entry => entry.Timestamp >= query.SinceUtc.Value);
        }

        if (query.UntilUtc.HasValue)
        {
            source = source.Where(entry => entry.Timestamp < query.UntilUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // EF.Functions.Like maps to SQL LIKE, which is case-insensitive for ASCII in SQLite.
            var pattern = $"%{EscapeLike(query.Search.Trim())}%";
            source = source.Where(entry =>
                EF.Functions.Like(entry.Message, pattern, LikeEscape)
                || EF.Functions.Like(entry.Category, pattern, LikeEscape)
                || (entry.NodeName != null && EF.Functions.Like(entry.NodeName, pattern, LikeEscape))
                || (entry.FlowName != null && EF.Functions.Like(entry.FlowName, pattern, LikeEscape))
                || (entry.IntegrationName != null && EF.Functions.Like(entry.IntegrationName, pattern, LikeEscape))
                || EF.Functions.Like(entry.EventName, pattern, LikeEscape)
                || EF.Functions.Like(entry.Component, pattern, LikeEscape)
                || EF.Functions.Like(entry.Operation, pattern, LikeEscape)
                || (entry.Exception != null && EF.Functions.Like(entry.Exception, pattern, LikeEscape)));
        }

        return source;
    }

    // Returns null when no (or an unrecognised) level was supplied — meaning "no filter".
    public static string[]? ExpandMinimumLevel(string? minimumLevel)
    {
        if (string.IsNullOrWhiteSpace(minimumLevel))
        {
            return null;
        }

        if (!Enum.TryParse<LogLevel>(minimumLevel.Trim(), ignoreCase: true, out var parsed)
            || parsed == LogLevel.None)
        {
            return null;
        }

        return SeverityOrder
            .Where(level => level >= parsed)
            .Select(level => level.ToString())
            .ToArray();
    }

    private const string LikeEscape = "\\";

    // SQLite LIKE has no bracket escaping; wildcards are neutralised with an ESCAPE character.
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
