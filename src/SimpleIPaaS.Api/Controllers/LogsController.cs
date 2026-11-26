using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/logs")]
public sealed class LogsController : ControllerBase
{
    // A cold-start / briefly unreachable cluster must not produce a 503 per poll without a trace of
    // the cause; this throttle keeps the real exception in the log at most once every 30 seconds.
    private static long _lastDegradedLogTicks;

    private readonly ILogRepository _repository;
    private readonly DatabaseLoggerProvider? _databaseLogger;
    private readonly ILogger<LogsController>? _logger;
    private readonly IMemoryCache? _cache;

    public LogsController(ILogRepository repository, IEnumerable<ILoggerProvider>? providers = null, ILogger<LogsController>? logger = null, IMemoryCache? cache = null)
    {
        _repository = repository;
        _cache = cache;
        _databaseLogger = providers?.OfType<DatabaseLoggerProvider>().FirstOrDefault();
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetLogs([FromQuery] string? level = null, [FromQuery] string? source = null, [FromQuery] string? service = null, [FromQuery] string? component = null, [FromQuery] string? eventName = null, [FromQuery] string? operation = null, [FromQuery] string? outcome = null, [FromQuery] string? search = null, [FromQuery] Guid? integrationId = null, [FromQuery] Guid? flowId = null, [FromQuery] Guid? flowExecutionId = null, [FromQuery] Guid? cardId = null, [FromQuery] Guid? stepExecutionId = null, [FromQuery] string? traceId = null, [FromQuery] int? invocation = null, [FromQuery] int? retryAttempt = null, [FromQuery] bool? isTest = null, [FromQuery] Guid? testCaseId = null, [FromQuery] Guid? testRunId = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? until = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default)
    {
        if (!TryBuildQuery(level, source, service, component, eventName, operation, outcome, search, integrationId, flowId, flowExecutionId, cardId, stepExecutionId, traceId, invocation, retryAttempt, isTest, testCaseId, testRunId, since, until, out var query, out var problem)) return problem!;
        return await ExecuteAsync(async () =>
        {
            var result = await _repository.GetPageAsync(query, page, pageSize, cancellationToken);
            return Ok(new LogPageDto { Items = result.Items.Select(ToDto).ToList(), Page = result.Page, PageSize = result.PageSize, Total = result.Total, Cursor = result.Cursor });
        });
    }

    [HttpGet("tail")]
    public async Task<IActionResult> Tail([FromQuery] long afterId = 0, [FromQuery] string? level = null, [FromQuery] string? source = null, [FromQuery] string? service = null, [FromQuery] string? component = null, [FromQuery] string? eventName = null, [FromQuery] string? operation = null, [FromQuery] string? outcome = null, [FromQuery] string? search = null, [FromQuery] Guid? integrationId = null, [FromQuery] Guid? flowId = null, [FromQuery] Guid? flowExecutionId = null, [FromQuery] Guid? cardId = null, [FromQuery] Guid? stepExecutionId = null, [FromQuery] string? traceId = null, [FromQuery] int? invocation = null, [FromQuery] int? retryAttempt = null, [FromQuery] bool? isTest = null, [FromQuery] Guid? testCaseId = null, [FromQuery] Guid? testRunId = null, [FromQuery] int limit = 200, CancellationToken cancellationToken = default)
    {
        if (afterId < 0) return ValidationProblem("afterId cannot be negative.");
        if (!TryBuildQuery(level, source, service, component, eventName, operation, outcome, search, integrationId, flowId, flowExecutionId, cardId, stepExecutionId, traceId, invocation, retryAttempt, isTest, testCaseId, testRunId, null, null, out var query, out var problem)) return problem!;
        return await ExecuteAsync(async () =>
        {
            var result = await _repository.GetTailAsync(afterId, query, limit, cancellationToken);
            return Ok(new LogTailDto { Entries = result.Entries.Select(ToDto).ToList(), Cursor = result.Cursor, Reset = result.Reset });
        });
    }

    [HttpGet("facets")]
    public async Task<IActionResult> Facets([FromQuery] string? level = null, [FromQuery] string? source = null, [FromQuery] string? service = null, [FromQuery] string? component = null, [FromQuery] string? eventName = null, [FromQuery] string? outcome = null, CancellationToken cancellationToken = default)
    {
        if (!TryBuildQuery(level, source, service, component, eventName, null, outcome, null, null, null, null, null, null, null, null, null, null, null, null, null, null, out var query, out var problem)) return problem!;
        return await ExecuteAsync(async () =>
        {
            // The facet lists are five DISTINCT scans over the log table and change slowly, while the console
            // asks for them whenever a filter changes. A short cache keeps filter changes instant.
            var cacheKey = $"log-facets|{level}|{source}|{service}|{component}|{eventName}|{outcome}";
            if (_cache != null && _cache.TryGetValue(cacheKey, out LogFacetsDto? cached) && cached != null) return Ok(cached);
            var facets = await _repository.GetFacetsAsync(query, cancellationToken);
            var dto = new LogFacetsDto { Services = facets.Services.ToList(), Components = facets.Components.ToList(), Events = facets.Events.ToList(), Outcomes = facets.Outcomes.ToList(), CardTypes = facets.CardTypes.ToList() };
            _cache?.Set(cacheKey, dto, TimeSpan.FromSeconds(15));
            return Ok(dto);
        });
    }

    [HttpGet("execution/{executionId:guid}/timeline")]
    public async Task<IActionResult> Timeline(Guid executionId, [FromQuery] int limit = 1000, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async () =>
        {
            var entries = await _repository.GetExecutionTimelineAsync(executionId, limit, cancellationToken);
            return Ok(new LogTimelineDto { ExecutionId = executionId, FlowName = entries.FirstOrDefault()?.FlowName, IntegrationName = entries.FirstOrDefault()?.IntegrationName, TraceId = entries.FirstOrDefault(entry => entry.TraceId != null)?.TraceId, Entries = entries.Select(ToDto).ToList() });
        });
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await _repository.GetTelemetryHealthAsync(cancellationToken);
            var recent = health.LatestEventAtUtc.HasValue && DateTime.UtcNow - health.LatestEventAtUtc.Value < TimeSpan.FromMinutes(5);
            var sink = _databaseLogger?.GetHealth();
            return Ok(new TelemetryHealthDto { LatestEventAtUtc = health.LatestEventAtUtc, StoredEventCount = health.StoredEventCount, RecentErrorCount = health.RecentErrorCount, DatabaseAvailable = true, Status = recent ? "Healthy" : "Stale", InstanceId = sink?.InstanceId, QueueDepth = sink?.QueueDepth ?? 0, DroppedEvents = sink?.DroppedEvents ?? 0, WriteFailures = sink?.WriteFailures ?? 0, WrittenEvents = sink?.WrittenEvents ?? 0, LastSuccessfulWriteAtUtc = sink?.LastSuccessfulWriteUtc });
        }
        catch (Exception)
        {
            return Ok(new TelemetryHealthDto { DatabaseAvailable = false, Status = "Unavailable" });
        }
    }

    // The log volume alert for the app banner (admins). The measurement is cached for two minutes: every open page
    // polls it, and counting the log table each time would add load for nothing.
    [HttpGet("volume")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Volume([FromServices] IStorageSettingsProvider settings, [FromServices] ILogVolumeProbe probe, CancellationToken cancellationToken = default)
    {
        var snapshot = await settings.GetAsync(cancellationToken);
        var logs = snapshot.For(StorageDatasets.Logs);
        var alertRows = StorageCatalog.LogAlertRows(logs);
        var result = new LogVolumeDto
        {
            Level = "Off",
            AlertRows = alertRows,
            RetentionDays = logs.RetentionDays,
            WarningRetentionDays = StorageCatalog.LogWarningRetentionDays(logs),
            CheckedAtUtc = DateTime.UtcNow
        };
        if (alertRows <= 0 || snapshot.UsesElasticsearch(StorageDatasets.Logs))
        {
            return Ok(result);
        }

        var volume = await MeasureCachedAsync(probe, includeBreakdown: false, cancellationToken);
        var level = Application.Services.LogVolumeAlertService.LevelFor(volume.StoredLines, alertRows);
        if (level > Application.Services.LogVolumeLevel.Normal)
        {
            volume = await MeasureCachedAsync(probe, includeBreakdown: true, cancellationToken);
            level = Application.Services.LogVolumeAlertService.LevelFor(volume.StoredLines, alertRows);
        }

        result.Level = level.ToString();
        result.StoredLines = volume.StoredLines;
        result.StoredBytes = volume.StoredBytes;
        result.LastDayLines = volume.LastDayLines;
        result.TopCategories = volume.TopCategories.Select(source => new LogVolumeSourceDto { Name = source.Name, Lines = source.Lines }).ToList();
        result.TopFlows = volume.TopFlows.Select(source => new LogVolumeSourceDto { Name = source.Name, Lines = source.Lines }).ToList();
        return Ok(result);
    }

    private async Task<LogVolume> MeasureCachedAsync(ILogVolumeProbe probe, bool includeBreakdown, CancellationToken cancellationToken)
    {
        var key = includeBreakdown ? "log-volume:detailed" : "log-volume";
        if (_cache?.TryGetValue(key, out LogVolume? cached) == true && cached != null)
        {
            return cached;
        }

        var measured = await probe.MeasureAsync(includeBreakdown, cancellationToken);
        _cache?.Set(key, measured, TimeSpan.FromMinutes(2));
        return measured;
    }

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async () => Ok(await _repository.GetSourcesAsync(cancellationToken)));

    [HttpGet("levels")]
    public IActionResult GetLevels() => Ok(LogRepository.LevelNames);

    [HttpDelete("")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Clear([FromQuery] DateTime? olderThan = null, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(async () =>
            Ok(new LogDeleteResultDto { Deleted = await _repository.DeleteAsync(olderThan.HasValue ? ToUtc(olderThan.Value) : null, cancellationToken) }));

    // An Elasticsearch-backed logs dataset that is momentarily unusable (cold start, cluster down,
    // wrong credentials) answers 503 with a retryable message instead of an unhandled 500, and the
    // real cause is written to the log (throttled so a polling client cannot flood it).
    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is StorageBackendUnavailableException or ElasticsearchRequestException)
        {
            LogDegraded(exception);
            return StorageUnavailable(exception);
        }
    }

    private void LogDegraded(Exception exception)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastDegradedLogTicks);
        if (last != 0 && now - last < TimeSpan.FromSeconds(30).Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastDegradedLogTicks, now, last) != last)
        {
            return;
        }

        _logger?.LogWarning(exception, "The logs endpoints cannot reach their Elasticsearch backend; answering 503 with the real cause.");
    }

    private static ObjectResult StorageUnavailable(Exception exception) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "The logs backend is not ready.",
            Detail = exception.Message
        })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };

    private bool TryBuildQuery(string? level, string? source, string? service, string? component, string? eventName, string? operation, string? outcome, string? search, Guid? integrationId, Guid? flowId, Guid? flowExecutionId, Guid? cardId, Guid? stepExecutionId, string? traceId, int? invocation, int? retryAttempt, bool? isTest, Guid? testCaseId, Guid? testRunId, DateTime? since, DateTime? until, out LogQuery query, out IActionResult? problem)
    {
        query = new LogQuery();
        problem = null;
        if (!string.IsNullOrWhiteSpace(level) && LogRepository.ExpandMinimumLevel(level) == null)
        {
            ModelState.AddModelError(nameof(level), $"'{level}' is not a valid log level.");
            problem = ValidationProblem(ModelState);
            return false;
        }
        query = new LogQuery { MinimumLevel = level, Source = source, Service = service, Component = component, EventName = eventName, Operation = operation, Outcome = outcome, Search = search, IntegrationId = integrationId, FlowId = flowId, FlowExecutionId = flowExecutionId, CardId = cardId, StepExecutionId = stepExecutionId, TraceId = traceId, Invocation = invocation, RetryAttempt = retryAttempt, IsTest = isTest, TestCaseId = testCaseId, TestRunId = testRunId, SinceUtc = since.HasValue ? ToUtc(since.Value) : null, UntilUtc = until.HasValue ? ToUtc(until.Value) : null };
        return true;
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch { DateTimeKind.Utc => value, DateTimeKind.Local => value.ToUniversalTime(), _ => DateTime.SpecifyKind(value, DateTimeKind.Utc) };

    private static AppLogEntryDto ToDto(AppLogEntry entry) => new()
    {
        Id = entry.Id, EventId = entry.EventId, EventVersion = entry.EventVersion, Timestamp = entry.Timestamp, Level = entry.Level, Source = entry.Source, Service = entry.Service, HostInstance = entry.HostInstance, EnvironmentName = entry.EnvironmentName, ApplicationVersion = entry.ApplicationVersion, Category = entry.Category, Component = entry.Component, EventName = entry.EventName, Operation = entry.Operation, Outcome = entry.Outcome, DurationMs = entry.DurationMs, TraceId = entry.TraceId, SpanId = entry.SpanId, ParentSpanId = entry.ParentSpanId, Message = entry.Message, Exception = entry.Exception, FlowExecutionId = entry.FlowExecutionId, FlowId = entry.FlowId, FlowName = entry.FlowName, IntegrationId = entry.IntegrationId, IntegrationName = entry.IntegrationName, ConnectionId = entry.ConnectionId, CardId = entry.CardId, CardType = entry.CardType, StepExecutionId = entry.StepExecutionId, Invocation = entry.Invocation, LoopPath = entry.LoopPath, RetryAttempt = entry.RetryAttempt, IsTest = entry.IsTest, TestCaseId = entry.TestCaseId, TestRunId = entry.TestRunId, NodeName = entry.NodeName, PropertiesJson = entry.PropertiesJson
    };
}