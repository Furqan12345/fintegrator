using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/logs")]
public sealed class LogsController : ControllerBase
{
    private readonly ILogRepository _repository;
    private readonly DatabaseLoggerProvider? _databaseLogger;

    public LogsController(ILogRepository repository, IEnumerable<ILoggerProvider>? providers = null)
    {
        _repository = repository;
        _databaseLogger = providers?.OfType<DatabaseLoggerProvider>().FirstOrDefault();
    }

    [HttpGet("")]
    public async Task<IActionResult> GetLogs([FromQuery] string? level = null, [FromQuery] string? source = null, [FromQuery] string? service = null, [FromQuery] string? component = null, [FromQuery] string? eventName = null, [FromQuery] string? operation = null, [FromQuery] string? outcome = null, [FromQuery] string? search = null, [FromQuery] Guid? integrationId = null, [FromQuery] Guid? flowId = null, [FromQuery] Guid? flowExecutionId = null, [FromQuery] Guid? cardId = null, [FromQuery] Guid? stepExecutionId = null, [FromQuery] string? traceId = null, [FromQuery] int? invocation = null, [FromQuery] int? retryAttempt = null, [FromQuery] bool? isTest = null, [FromQuery] Guid? testCaseId = null, [FromQuery] Guid? testRunId = null, [FromQuery] DateTime? since = null, [FromQuery] DateTime? until = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default)
    {
        if (!TryBuildQuery(level, source, service, component, eventName, operation, outcome, search, integrationId, flowId, flowExecutionId, cardId, stepExecutionId, traceId, invocation, retryAttempt, isTest, testCaseId, testRunId, since, until, out var query, out var problem)) return problem!;
        var result = await _repository.GetPageAsync(query, page, pageSize, cancellationToken);
        return Ok(new LogPageDto { Items = result.Items.Select(ToDto).ToList(), Page = result.Page, PageSize = result.PageSize, Total = result.Total, Cursor = result.Cursor });
    }

    [HttpGet("tail")]
    public async Task<IActionResult> Tail([FromQuery] long afterId = 0, [FromQuery] string? level = null, [FromQuery] string? source = null, [FromQuery] string? service = null, [FromQuery] string? component = null, [FromQuery] string? eventName = null, [FromQuery] string? operation = null, [FromQuery] string? outcome = null, [FromQuery] string? search = null, [FromQuery] Guid? integrationId = null, [FromQuery] Guid? flowId = null, [FromQuery] Guid? flowExecutionId = null, [FromQuery] Guid? cardId = null, [FromQuery] Guid? stepExecutionId = null, [FromQuery] string? traceId = null, [FromQuery] int? invocation = null, [FromQuery] int? retryAttempt = null, [FromQuery] bool? isTest = null, [FromQuery] Guid? testCaseId = null, [FromQuery] Guid? testRunId = null, [FromQuery] int limit = 200, CancellationToken cancellationToken = default)
    {
        if (afterId < 0) return ValidationProblem("afterId cannot be negative.");
        if (!TryBuildQuery(level, source, service, component, eventName, operation, outcome, search, integrationId, flowId, flowExecutionId, cardId, stepExecutionId, traceId, invocation, retryAttempt, isTest, testCaseId, testRunId, null, null, out var query, out var problem)) return problem!;
        var result = await _repository.GetTailAsync(afterId, query, limit, cancellationToken);
        return Ok(new LogTailDto { Entries = result.Entries.Select(ToDto).ToList(), Cursor = result.Cursor, Reset = result.Reset });
    }

    [HttpGet("facets")]
    public async Task<IActionResult> Facets([FromQuery] string? level = null, [FromQuery] string? source = null, [FromQuery] string? service = null, [FromQuery] string? component = null, [FromQuery] string? eventName = null, [FromQuery] string? outcome = null, CancellationToken cancellationToken = default)
    {
        if (!TryBuildQuery(level, source, service, component, eventName, null, outcome, null, null, null, null, null, null, null, null, null, null, null, null, null, null, out var query, out var problem)) return problem!;
        var facets = await _repository.GetFacetsAsync(query, cancellationToken);
        return Ok(new LogFacetsDto { Services = facets.Services.ToList(), Components = facets.Components.ToList(), Events = facets.Events.ToList(), Outcomes = facets.Outcomes.ToList(), CardTypes = facets.CardTypes.ToList() });
    }

    [HttpGet("execution/{executionId:guid}/timeline")]
    public async Task<IActionResult> Timeline(Guid executionId, [FromQuery] int limit = 1000, CancellationToken cancellationToken = default)
    {
        var entries = await _repository.GetExecutionTimelineAsync(executionId, limit, cancellationToken);
        return Ok(new LogTimelineDto { ExecutionId = executionId, FlowName = entries.FirstOrDefault()?.FlowName, IntegrationName = entries.FirstOrDefault()?.IntegrationName, TraceId = entries.FirstOrDefault(entry => entry.TraceId != null)?.TraceId, Entries = entries.Select(ToDto).ToList() });
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

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken = default) => Ok(await _repository.GetSourcesAsync(cancellationToken));

    [HttpGet("levels")]
    public IActionResult GetLevels() => Ok(LogRepository.LevelNames);

    [HttpDelete("")]
    public async Task<IActionResult> Clear([FromQuery] DateTime? olderThan = null, CancellationToken cancellationToken = default) => Ok(new LogDeleteResultDto { Deleted = await _repository.DeleteAsync(olderThan.HasValue ? ToUtc(olderThan.Value) : null, cancellationToken) });

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
        Id = entry.Id, EventId = entry.EventId, EventVersion = entry.EventVersion, Timestamp = entry.Timestamp, Level = entry.Level, Source = entry.Source, Service = entry.Service, HostInstance = entry.HostInstance, EnvironmentName = entry.EnvironmentName, ApplicationVersion = entry.ApplicationVersion, Category = entry.Category, Component = entry.Component, EventName = entry.EventName, Operation = entry.Operation, Outcome = entry.Outcome, DurationMs = entry.DurationMs, TraceId = entry.TraceId, SpanId = entry.SpanId, ParentSpanId = entry.ParentSpanId, Message = entry.Message, Exception = entry.Exception, TenantId = entry.TenantId, FlowExecutionId = entry.FlowExecutionId, FlowId = entry.FlowId, FlowName = entry.FlowName, IntegrationId = entry.IntegrationId, IntegrationName = entry.IntegrationName, ConnectionId = entry.ConnectionId, CardId = entry.CardId, CardType = entry.CardType, StepExecutionId = entry.StepExecutionId, Invocation = entry.Invocation, LoopPath = entry.LoopPath, RetryAttempt = entry.RetryAttempt, IsTest = entry.IsTest, TestCaseId = entry.TestCaseId, TestRunId = entry.TestRunId, NodeName = entry.NodeName, PropertiesJson = entry.PropertiesJson
    };
}