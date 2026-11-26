using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Counts what the relational log table holds, for the log volume alert. The total is an exact count (the log
// health endpoint already counts the table the same way); the bytes come from the day partitions on PostgreSQL.
public sealed class RelationalLogVolumeProbe : ILogVolumeProbe
{
    private const int TopSources = 5;

    private readonly IPaaSContext _context;

    public RelationalLogVolumeProbe(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<LogVolume> MeasureAsync(bool includeBreakdown, CancellationToken cancellationToken = default)
    {
        var logs = _context.AppLogEntries.AsNoTracking();
        var since = DateTime.UtcNow.AddHours(-24);
        var stored = await logs.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var lastDay = await logs.LongCountAsync(entry => entry.Timestamp >= since, cancellationToken).ConfigureAwait(false);
        var bytes = _context.IsPostgres() ? await PostgresBytesAsync(cancellationToken).ConfigureAwait(false) : (long?)null;
        if (!includeBreakdown)
        {
            return new LogVolume(stored, bytes, lastDay, Array.Empty<LogVolumeSource>(), Array.Empty<LogVolumeSource>());
        }

        // Every category of the day in one query (there are a few dozen), so the day's total and the shares come from
        // the same moment: lines keep arriving while this runs, and separate counts would not add up.
        var recent = logs.Where(entry => entry.Timestamp >= since);
        var categories = await recent
            .GroupBy(entry => entry.Category)
            .Select(group => new { Name = group.Key, Lines = group.LongCount() })
            .OrderByDescending(item => item.Lines)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        lastDay = categories.Sum(item => item.Lines);
        var flows = await recent
            .Where(entry => entry.FlowId != null)
            .GroupBy(entry => entry.FlowId)
            .Select(group => new { Name = group.Max(entry => entry.FlowName), Lines = group.LongCount() })
            .OrderByDescending(item => item.Lines)
            .Take(TopSources)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new LogVolume(
            stored,
            bytes,
            lastDay,
            categories.Take(TopSources).Select(item => new LogVolumeSource(string.IsNullOrWhiteSpace(item.Name) ? "(no category)" : item.Name, item.Lines)).ToList(),
            flows.Select(item => new LogVolumeSource(string.IsNullOrWhiteSpace(item.Name) ? "(unnamed flow)" : item.Name!, item.Lines)).ToList());
    }

    private async Task<long> PostgresBytesAsync(CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT COALESCE(SUM(pg_total_relation_size(inheritance.inhrelid)), 0)::bigint FROM pg_inherits inheritance " +
                $"WHERE inheritance.inhparent IN (to_regclass('\"{PostgresLayout.AppLogWarnings}\"'), to_regclass('\"{PostgresLayout.AppLogDetail}\"'));";
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long value ? value : 0;
        }
        finally
        {
            if (opened)
            {
                await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
