using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/deadletters")]
public class DeadLetterController : ControllerBase
{
    private readonly IExecutionRepository _repository;
    private readonly IIntegrationRepository _integrationRepository;
    private readonly ILogger<DeadLetterController> _logger;

    public DeadLetterController(
        IExecutionRepository repository,
        IIntegrationRepository integrationRepository,
        ILogger<DeadLetterController> logger)
    {
        _repository = repository;
        _integrationRepository = integrationRepository;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? status = null,
        [FromQuery] Guid? flowId = null,
        [FromQuery] Guid? integrationId = null,
        [FromQuery] string? search = null,
        [FromQuery] int? hours = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? executionId = null,
        [FromQuery] Guid? stepId = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 200) pageSize = 50;
        var rows = await _repository.GetDeadLetterPageAsync(new DeadLetterPageFilter(
            string.IsNullOrWhiteSpace(status) ? null : status,
            flowId,
            integrationId,
            search?.Trim(),
            hours,
            page,
            pageSize,
            executionId,
            stepId));
        return Ok(rows.Select(row => ToDto(row.Entry, row.FlowId ?? Guid.Empty)).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var entry = await _repository.GetDeadLetterAsync(id);
        if (entry == null) return NotFound();
        var execution = await _repository.GetFlowExecutionAsync(entry.FlowExecutionId);
        if (execution?.IsTest == true) return NotFound();

        var detail = new DeadLetterDetailDto
        {
            FlowStateJson = entry.FlowStateJson,
            PersistedStateJson = entry.PersistedStateJson,
            ReplayRequestedAt = entry.ReplayRequestedAt,
            ReplayFromStepId = entry.ReplayFromStepId,
            TriggerSource = execution?.TriggerSource ?? string.Empty,
            TriggerPayloadJson = execution?.TriggerPayloadJson ?? string.Empty,
            Attempts = ParseAttempts(entry.AttemptHistoryJson)
        };
        Copy(entry, execution?.FlowId ?? default, detail);

        if (execution != null)
        {
            var steps = await _repository.GetStepExecutionSummariesAsync(execution.Id);
            var failedStep = steps
                .Where(step => step.StepId == entry.StepId)
                .OrderByDescending(step => step.StartedAt)
                .FirstOrDefault();
            if (failedStep != null)
            {
                var payload = await _repository.GetStepPayloadAsync(execution.Id, failedStep.Id);
                detail.FailedStep = new DeadLetterFailedStepDto
                {
                    StepExecutionId = failedStep.Id,
                    Status = failedStep.Status.ToString(),
                    HttpStatusCode = failedStep.HttpStatusCode,
                    StartedAt = failedStep.StartedAt,
                    CompletedAt = failedStep.CompletedAt,
                    ErrorMessage = failedStep.ErrorMessage,
                    ReceivedInput = payload?.ReceivedInput ?? string.Empty,
                    RequestPayload = payload?.RequestPayload ?? string.Empty,
                    ResponsePayload = payload?.ResponsePayload ?? string.Empty
                };

                var packets = await _repository.GetStepPacketLogSummariesAsync(execution.Id);
                detail.Packets = packets
                    .Where(packet => packet.StepExecutionId == failedStep.Id)
                    .OrderBy(packet => packet.Sequence)
                    .Select(packet => new DeadLetterPacketDto
                    {
                        PacketId = packet.Id,
                        PageNumber = packet.PageNumber,
                        Attempt = packet.Attempt,
                        HttpMethod = packet.HttpMethod,
                        RequestUrl = packet.RequestUrl,
                        StatusCode = packet.StatusCode,
                        DurationMs = packet.DurationMs,
                        Error = packet.Error,
                        StartedAt = packet.StartedAt
                    })
                    .ToList();
            }
        }

        return Ok(detail);
    }

    [HttpGet("{id:guid}/resync-nodes")]
    public async Task<IActionResult> GetResyncNodes(Guid id)
    {
        var context = await GetResyncContextAsync(id);
        if (context == null || context.Execution.IsTest) return NotFound();
        if (!IsReplayableStatus(context.Entry.Status))
        {
            return Problem(title: "Not replayable", detail: $"This entry is '{context.Entry.Status}'.", statusCode: StatusCodes.Status409Conflict);
        }

        return Ok(await BuildResyncNodeOptionsAsync(context));
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, [FromBody] ResyncDeadLetterRequestDto? request)
    {
        var context = await GetResyncContextAsync(id);
        if (context == null) return NotFound();
        if (context.Execution.IsTest)
        {
            return BadRequest(new { message = "Test-run dead letters cannot be replayed." });
        }
        var entry = context.Entry;

        if (!IsReplayableStatus(entry.Status))
        {
            return Problem(
                title: "Not replayable",
                detail: $"Only Pending or Retrying entries can be replayed; this entry is '{entry.Status}'.",
                statusCode: StatusCodes.Status409Conflict);
        }

        string? editedFlowState = null;
        if (request?.FlowStateJson is not null)
        {
            if (!TryValidateFlowStateJson(request.FlowStateJson, out var flowStateError))
            {
                return Problem(
                    title: "Invalid flow state",
                    detail: flowStateError,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            editedFlowState = request.FlowStateJson;
        }

        var outcome = await TryQueueReplayAsync(
            entry, context.Execution, context.Flow, request?.StartStepId, editedFlowState, dryRun: false);

        switch (outcome.Result)
        {
            case ReplayQueueResult.NoResyncNode:
                return Problem(
                    title: "No resync node available",
                    detail: outcome.Detail,
                    statusCode: StatusCodes.Status409Conflict);
            case ReplayQueueResult.InvalidStartNode:
                return Problem(
                    title: "Invalid resync node",
                    detail: outcome.Detail,
                    statusCode: StatusCodes.Status400BadRequest);
            case ReplayQueueResult.ReplayNotPermitted:
                return Problem(
                    title: "Replay not permitted",
                    detail: outcome.Detail,
                    statusCode: StatusCodes.Status400BadRequest);
            case ReplayQueueResult.NotReplayable:
                return Problem(
                    title: "Not replayable",
                    detail: outcome.Detail,
                    statusCode: StatusCodes.Status409Conflict);
            case ReplayQueueResult.TestRun:
                return BadRequest(new { message = outcome.Detail });
        }

        // Idempotent replay request: the stamp only applies while no replay is already queued, so
        // two operators (or a double click) end up with exactly one replay. The loser is told the
        // request was already accepted instead of queueing a second run.
        var current = await _repository.GetDeadLetterAsync(entry.Id) ?? entry;
        var dto = ToDto(current, context.Flow.Id);
        dto.ReplayAlreadyQueued = outcome.Result == ReplayQueueResult.AlreadyQueued;
        if (outcome.Result == ReplayQueueResult.AlreadyQueued)
        {
            _logger.LogInformation(
                "Dead letter {DeadLetterId} already has a replay queued; the repeated retry request was accepted as a no-op.",
                entry.Id);
        }

        return Accepted(dto);
    }

    [HttpPost("bulk-retry")]
    public async Task<IActionResult> BulkRetry([FromBody] BulkRetryDeadLettersRequestDto? request)
    {
        if (request == null)
        {
            return BadRequest(new { message = "A request body is required." });
        }

        var ids = request.Ids?.Where(id => id != Guid.Empty).Distinct().ToList() ?? new List<Guid>();
        var hasIds = ids.Count > 0;
        var hasFlowId = request.FlowId.HasValue && request.FlowId.Value != Guid.Empty;
        var hasExecutionId = request.ExecutionId.HasValue && request.ExecutionId.Value != Guid.Empty;
        var hasFilter = hasFlowId || hasExecutionId;

        // Exactly one mode: explicit ids OR a filter anchored on FlowId or ExecutionId.
        if (hasIds == hasFilter)
        {
            return BadRequest(new
            {
                message = "Send either Ids or a FlowId/ExecutionId filter, not both, so a bulk replay cannot target the whole deployment by accident."
            });
        }

        if (hasIds && ids.Count > 500)
        {
            return BadRequest(new { message = "Select at most 500 dead letters." });
        }

        var search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim();
        const int filterPageSize = 500;
        const int filterMaxEntries = 5000;

        Task<IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)>> LoadFilterPageAsync(int page, int pageSize) =>
            _repository.GetDeadLetterPageAsync(new DeadLetterPageFilter(
                Status: "Pending",
                FlowId: request.FlowId,
                Search: search,
                Hours: request.Hours,
                Page: page,
                PageSize: pageSize,
                FlowExecutionId: request.ExecutionId,
                StepId: request.StepId));

        List<(DeadLetterEntry Entry, Guid? FlowId)> candidates;
        var truncated = false;

        if (hasIds)
        {
            candidates = await LoadDeadLettersByIdsAsync(ids);
        }
        else
        {
            candidates = new List<(DeadLetterEntry Entry, Guid? FlowId)>();
            for (var page = 1; page <= filterMaxEntries / filterPageSize; page++)
            {
                var rows = await LoadFilterPageAsync(page, filterPageSize);
                candidates.AddRange(rows);
                if (rows.Count < filterPageSize)
                {
                    break;
                }
            }

            if (candidates.Count >= filterMaxEntries)
            {
                var overflow = await LoadFilterPageAsync(filterMaxEntries + 1, 1);
                truncated = overflow.Count > 0;
            }
        }

        var executionCache = new Dictionary<Guid, FlowExecution?>();
        var flowCache = new Dictionary<Guid, IntegrationFlow?>();
        var resolved = new List<(DeadLetterEntry Entry, FlowExecution Execution, IntegrationFlow Flow)>();
        var skips = new List<BulkRetrySkipDto>();
        var skippedCount = 0;

        foreach (var candidate in candidates)
        {
            if (!executionCache.TryGetValue(candidate.Entry.FlowExecutionId, out var execution))
            {
                execution = await _repository.GetFlowExecutionAsync(candidate.Entry.FlowExecutionId);
                executionCache[candidate.Entry.FlowExecutionId] = execution;
            }

            if (execution == null)
            {
                skippedCount++;
                skips.Add(new BulkRetrySkipDto
                {
                    Id = candidate.Entry.Id,
                    NodeName = candidate.Entry.NodeName,
                    Reason = "The owning execution no longer exists."
                });
                continue;
            }

            if (execution.IsTest)
            {
                return BadRequest(new { message = "Test-run dead letters cannot be replayed." });
            }

            if (!flowCache.TryGetValue(execution.FlowId, out var flow))
            {
                flow = await _integrationRepository.GetByIdAsync(execution.FlowId);
                flowCache[execution.FlowId] = flow;
            }

            if (flow == null)
            {
                skippedCount++;
                skips.Add(new BulkRetrySkipDto
                {
                    Id = candidate.Entry.Id,
                    NodeName = candidate.Entry.NodeName,
                    Reason = "The owning flow no longer exists."
                });
                continue;
            }

            resolved.Add((candidate.Entry, execution, flow));
        }

        if (request.StartStepId.HasValue && resolved.Select(item => item.Flow.Id).Distinct().Count() > 1)
        {
            return BadRequest(new
            {
                message = "StartStepId can only be used when every selected dead letter belongs to one flow."
            });
        }

        if (hasIds)
        {
            var found = candidates.Select(candidate => candidate.Entry.Id).ToHashSet();
            foreach (var missingId in ids.Where(id => !found.Contains(id)))
            {
                skippedCount++;
                skips.Add(new BulkRetrySkipDto
                {
                    Id = missingId,
                    Reason = "Not found."
                });
            }
        }

        var result = new BulkRetryDeadLettersResultDto
        {
            Matched = candidates.Count,
            TotalMatched = candidates.Count,
            Truncated = truncated
        };

        foreach (var item in resolved)
        {
            var outcome = await TryQueueReplayAsync(
                item.Entry, item.Execution, item.Flow, request.StartStepId, editedFlowState: null, request.DryRun);

            switch (outcome.Result)
            {
                case ReplayQueueResult.Queued:
                    result.Queued++;
                    if (!request.DryRun)
                    {
                        result.QueuedIds.Add(item.Entry.Id);
                    }
                    break;
                case ReplayQueueResult.AlreadyQueued:
                    result.AlreadyQueued++;
                    break;
                default:
                    skippedCount++;
                    skips.Add(new BulkRetrySkipDto
                    {
                        Id = item.Entry.Id,
                        NodeName = item.Entry.NodeName,
                        Reason = outcome.Detail
                    });
                    break;
            }
        }

        result.Skipped = skippedCount;
        result.Skips = skips.Take(50).ToList();

        _logger.LogInformation(
            "Bulk dead-letter replay ({Mode}, dry run: {DryRun}): matched {Matched}, truncated {Truncated}, queued {Queued}, already queued {AlreadyQueued}, skipped {Skipped}.",
            hasIds ? "selection" : "filter",
            request.DryRun, result.Matched, result.Truncated, result.Queued, result.AlreadyQueued, result.Skipped);

        return Ok(result);
    }

    private async Task<List<(DeadLetterEntry Entry, Guid? FlowId)>> LoadDeadLettersByIdsAsync(
        IReadOnlyCollection<Guid> ids)
    {
        var rows = new List<(DeadLetterEntry Entry, Guid? FlowId)>();
        foreach (var id in ids)
        {
            // Bulk replay only acts on rows the operator explicitly selected.
            var entry = await _repository.GetDeadLetterAsync(id);
            if (entry != null)
            {
                rows.Add((entry, null));
            }
        }

        return rows;
    }

    private async Task<ReplayQueueAttempt> TryQueueReplayAsync(
        DeadLetterEntry entry,
        FlowExecution execution,
        IntegrationFlow flow,
        Guid? requestedStartStepId,
        string? editedFlowState,
        bool dryRun)
    {
        if (execution.IsTest)
        {
            return new ReplayQueueAttempt(
                ReplayQueueResult.TestRun,
                "Test-run dead letters cannot be replayed.");
        }

        if (!IsReplayableStatus(entry.Status))
        {
            return new ReplayQueueAttempt(
                ReplayQueueResult.NotReplayable,
                $"Only Pending or Retrying entries can be replayed; this entry is '{entry.Status}'.");
        }

        if (entry.ReplayRequestedAt != null)
        {
            return new ReplayQueueAttempt(
                ReplayQueueResult.AlreadyQueued,
                "A replay is already queued for this dead letter.");
        }

        var candidates = await BuildResyncNodeOptionsAsync(new ResyncContext(entry, execution, flow));
        var startStepId = requestedStartStepId
            ?? candidates.FirstOrDefault(option => option.IsDefault)?.StepId
            ?? candidates.FirstOrDefault()?.StepId;
        if (!startStepId.HasValue)
        {
            return new ReplayQueueAttempt(
                ReplayQueueResult.NoResyncNode,
                "This dead letter has no eligible completed node or loop to resync from.");
        }

        if (!candidates.Any(option => option.StepId == startStepId.Value))
        {
            return new ReplayQueueAttempt(
                ReplayQueueResult.InvalidStartNode,
                "Choose the failed node, the loop that contains it, or a previously completed node upstream of it. Nodes inside a loop body can only be replayed from the loop card.");
        }

        var rerunNodeIds = FlowGraphAnalysis.GetDownstreamNodeIds(flow, startStepId.Value);
        var unsafeReplayNodes = flow.Nodes
            .Where(node =>
                rerunNodeIds.Contains(node.Id) &&
                ReplaySafety.IsUnsafeReplayNode(node, flow.AllowPostReplay))
            .ToList();
        if (unsafeReplayNodes.Count > 0)
        {
            if (unsafeReplayNodes.Any(node => node.StepType == StepType.Email))
            {
                return new ReplayQueueAttempt(
                    ReplayQueueResult.ReplayNotPermitted,
                    "Replay would resend an email");
            }

            var details = string.Join(", ", unsafeReplayNodes.Select(node => $"'{node.NodeName}' ({node.HttpMethod})"));
            return new ReplayQueueAttempt(
                ReplayQueueResult.ReplayNotPermitted,
                $"The resync path includes side-effecting node(s) {details}. Enable post replay in flow settings to allow replaying these calls.");
        }

        if (dryRun)
        {
            return new ReplayQueueAttempt(ReplayQueueResult.Queued, "Would be queued.", startStepId);
        }

        var queued = await _repository.TryRequestManualReplayAsync(
            entry.Id, startStepId.Value, DateTime.UtcNow, editedFlowState);

        return queued
            ? new ReplayQueueAttempt(ReplayQueueResult.Queued, "Replay queued.", startStepId)
            : new ReplayQueueAttempt(
                ReplayQueueResult.AlreadyQueued,
                "A replay is already queued for this dead letter.",
                startStepId);
    }

    [HttpPost("{id:guid}/discard")]
    public async Task<IActionResult> Discard(Guid id)
    {
        var entry = await _repository.GetDeadLetterAsync(id);
        if (entry == null) return NotFound();
        if (entry.Status == "Resolved")
        {
            return Problem(
                title: "Already resolved",
                detail: "This dead letter was resolved by a successful replay and cannot be discarded.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Resolve the owning flow so the discarded row reports its flow (it used to come back as
        // 00000000-… and rendered as a blank flow in the UI).
        var execution = await _repository.GetFlowExecutionAsync(entry.FlowExecutionId);

        if (entry.Status == "Discarded")
        {
            return Ok(ToDto(entry, execution?.FlowId ?? Guid.Empty));
        }

        entry.Status = "Discarded";
        entry.ReplayRequestedAt = null;
        await _repository.UpdateDeadLetterEntryAsync(entry);
        return Ok(ToDto(entry, execution?.FlowId ?? Guid.Empty));
    }

    private async Task<ResyncContext?> GetResyncContextAsync(Guid id)
    {
        var entry = await _repository.GetDeadLetterAsync(id);
        if (entry == null) return null;
        var execution = await _repository.GetFlowExecutionAsync(entry.FlowExecutionId);
        if (execution == null) return null;
        var flow = await _integrationRepository.GetByIdAsync(execution.FlowId);
        return flow == null ? null : new ResyncContext(entry, execution, flow);
    }

    private async Task<List<ResyncNodeOptionDto>> BuildResyncNodeOptionsAsync(ResyncContext context)
    {
        var flow = context.Flow;
        var entry = context.Entry;
        var ancestors = FlowGraphAnalysis.GetAncestorNodeIds(flow, entry.StepId);
        var loopBodyNodeIds = FlowGraphAnalysis.GetAllForEachBodyNodeIds(flow);
        var containingLoopId = FlowGraphAnalysis.GetOutermostForEachNodeId(flow, entry.StepId);
        var completedNodeIds = (await _repository.GetStepExecutionsAsync(context.Execution.Id))
            .Where(step => step.Status == ExecutionStatus.Success || step.Status == ExecutionStatus.Recovered)
            .Select(step => step.StepId)
            .ToHashSet();

        // A failed node inside a ForEach only ever ran as part of an iteration, so it is not a
        // valid start point. Its outermost loop is offered instead so the whole loop re-runs.
        var candidates = flow.Nodes
            .Where(node =>
                ancestors.Contains(node.Id) &&
                !loopBodyNodeIds.Contains(node.Id) &&
                (node.Id == entry.StepId ||
                 node.Id == containingLoopId ||
                 completedNodeIds.Contains(node.Id)))
            .ToList();

        var defaultStepId = ResolveDefaultStepId(flow, entry, candidates, containingLoopId, loopBodyNodeIds);

        return candidates
            .Select(node => new ResyncNodeOptionDto
            {
                StepId = node.Id,
                NodeName = string.IsNullOrWhiteSpace(node.NodeName) ? node.StepType.ToString() : node.NodeName,
                StepType = node.StepType.ToString(),
                IsFailedNode = node.Id == entry.StepId,
                ContainsFailedNode = containingLoopId is Guid loopId && node.Id == loopId,
                IsDefault = node.Id == defaultStepId
            })
            .ToList();
    }

    private static Guid? ResolveDefaultStepId(
        IntegrationFlow flow,
        DeadLetterEntry entry,
        List<IntegrationStep> candidates,
        Guid? containingLoopId,
        HashSet<Guid> loopBodyNodeIds)
    {
        if (flow.DefaultDeadLetterResyncStepId is Guid configuredId)
        {
            if (loopBodyNodeIds.Contains(configuredId))
            {
                configuredId = FlowGraphAnalysis.GetOutermostForEachNodeId(flow, configuredId) ?? configuredId;
            }

            if (candidates.Any(node => node.Id == configuredId))
            {
                return configuredId;
            }
        }

        if (candidates.Any(node => node.Id == entry.StepId))
        {
            return entry.StepId;
        }

        if (containingLoopId is Guid loopId && candidates.Any(node => node.Id == loopId))
        {
            return loopId;
        }

        return candidates.FirstOrDefault()?.Id;
    }

    private static bool TryValidateFlowStateJson(string json, out string error)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Flow state must be a JSON object.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Flow state must be a JSON object.";
                return false;
            }
        }
        catch (JsonException ex)
        {
            error = "Flow state is not valid JSON: " + ex.Message;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsReplayableStatus(string status) => status == "Pending" || status == "Retrying";

    private static List<DeadLetterAttemptDto> ParseAttempts(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<DeadLetterAttemptDto>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<DeadLetterAttemptDto>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<DeadLetterAttemptDto>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<DeadLetterAttemptDto>();
        }
    }

    private static void Copy(DeadLetterEntry entry, Guid flowId, DeadLetterDto dto)
    {
        dto.Id = entry.Id;
        dto.FlowId = flowId;
        dto.FlowExecutionId = entry.FlowExecutionId;
        dto.StepId = entry.StepId;
        dto.FlowName = entry.FlowName;
        dto.IntegrationName = entry.IntegrationName;
        dto.NodeName = entry.NodeName;
        dto.Payload = entry.Payload;
        dto.ErrorMessage = entry.ErrorMessage;
        dto.AttemptHistoryJson = entry.AttemptHistoryJson;
        dto.RetryCount = entry.RetryCount;
        dto.MaxAttempts = DeadLetterService.MaxReplayAttemptsBeforeDiscard;
        dto.ReplayQueued = entry.ReplayRequestedAt != null;
        dto.CreatedAt = entry.CreatedAt;
        dto.LastRetriedAt = entry.LastRetriedAt;
        dto.ResolvedAt = entry.ResolvedAt;
        dto.Status = entry.Status;
    }

    private static DeadLetterDto ToDto(DeadLetterEntry entry, Guid flowId = default)
    {
        var dto = new DeadLetterDto();
        Copy(entry, flowId, dto);
        return dto;
    }

    private enum ReplayQueueResult
    {
        Queued,
        AlreadyQueued,
        NotReplayable,
        NoResyncNode,
        InvalidStartNode,
        ReplayNotPermitted,
        TestRun
    }

    private sealed record ReplayQueueAttempt(ReplayQueueResult Result, string Detail, Guid? StartStepId = null);

    private sealed record ResyncContext(DeadLetterEntry Entry, FlowExecution Execution, IntegrationFlow Flow);
}
