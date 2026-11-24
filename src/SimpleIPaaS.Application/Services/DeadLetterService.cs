using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public enum DeadLetterReplayOutcome
{
    NotFound,
    Skipped,
    PostReplayNotAllowed,
    Replayed
}

public class DeadLetterReplayResult
{
    public DeadLetterReplayOutcome Outcome { get; init; }
    public DeadLetterEntry? Entry { get; init; }
}

public class DeadLetterAttempt
{
    public int Attempt { get; set; }
    public DateTime At { get; set; }
    public int? StatusCode { get; set; }
    public string Error { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    // Replay | Blocked | Failure — how this attempt came about.
    public string Kind { get; set; } = "Replay";
}

public class DeadLetterService
{
    private static readonly string[] OutstandingStatuses = { "Pending", "Retrying" };
    private static readonly JsonSerializerOptions AttemptHistoryOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Attempt history may be seeded by the executor in PascalCase; read any
        // casing so fields like Kind/NodeName are never silently defaulted.
        PropertyNameCaseInsensitive = true
    };

    private readonly IExecutionRepository _executionRepository;
    private readonly IIntegrationRepository _integrationRepository;
    private readonly FlowExecutor _flowExecutor;
    private readonly ILogger<DeadLetterService> _logger;
    private readonly int _maxAutoRetryAttempts;

    public DeadLetterService(
        IExecutionRepository executionRepository,
        IIntegrationRepository integrationRepository,
        FlowExecutor flowExecutor,
        ILogger<DeadLetterService> logger,
        IConfiguration? configuration = null)
    {
        _executionRepository = executionRepository;
        _integrationRepository = integrationRepository;
        _flowExecutor = flowExecutor;
        _logger = logger;
        _maxAutoRetryAttempts = configuration?.GetValue("DeadLetter:MaxAutoRetryAttempts", 0) ?? 0;
    }

    public bool CanAutoRetry(DeadLetterEntry entry) => !entry.ManualReplayOnly && entry.RetryCount < _maxAutoRetryAttempts;

    // Shared with API/UI so the discard threshold has a single source of truth.
    public const int MaxReplayAttemptsBeforeDiscard = 5;

    public Task<DeadLetterReplayResult> ReplayEntryAsync(
        Guid entryId,
        bool force,
        CancellationToken cancellationToken = default) =>
        ReplayEntryAsync(entryId, force, null, cancellationToken);

    public async Task<DeadLetterReplayResult> ReplayEntryAsync(
        Guid entryId,
        bool force,
        Guid? requestedStartStepId,
        CancellationToken cancellationToken)
    {
        var entry = await _executionRepository.GetDeadLetterAsync(entryId);
        if (entry == null)
        {
            return new DeadLetterReplayResult { Outcome = DeadLetterReplayOutcome.NotFound };
        }

        if (entry.Status != "Pending" && entry.Status != "Retrying")
        {
            return await SkipAsync(entry);
        }

        if (!force && (entry.ManualReplayOnly || entry.RetryCount >= _maxAutoRetryAttempts))
        {
            _logger.LogInformation(
                "Dead letter {DeadLetterId} left for manual review; automatic retries are disabled (limit {MaxAutoRetryAttempts}).",
                entry.Id, _maxAutoRetryAttempts);
            return await SkipAsync(entry);
        }

        var flowExecution = await _executionRepository.GetFlowExecutionAsync(entry.FlowExecutionId);
        if (flowExecution == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "FlowExecution not found";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return Skipped(entry);
        }

        var flow = await _integrationRepository.GetByIdAsync(flowExecution.FlowId);
        if (flow == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "IntegrationFlow not found";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return Skipped(entry);
        }

        var startStepId = await ResolveStartNodeAsync(entry, flow, requestedStartStepId);
        if (!startStepId.HasValue)
        {
            _logger.LogWarning("Resync start node is not an eligible completed upstream node of dead letter {DeadLetterId}",
                entry.Id);
            return await SkipAsync(entry);
        }

        var rerunNodeIds = FlowGraphAnalysis.GetDownstreamNodeIds(flow, startStepId.Value);
        var unsafeReplayNode = flow.Nodes.FirstOrDefault(node =>
            rerunNodeIds.Contains(node.Id) &&
            ReplaySafety.IsUnsafeReplayNode(node, flow.AllowPostReplay));
        if (unsafeReplayNode != null)
        {
            // Never loop silently: the reason is recorded once on the entry so the
            // user can see why auto-retry keeps refusing this replay.
            AppendBlockedNoteOnce(entry,
                unsafeReplayNode.StepType == StepType.Email
                    ? $"Replay blocked: '{unsafeReplayNode.NodeName}' would resend an email and the flow does not allow post-replay."
                    : $"Replay blocked: '{unsafeReplayNode.NodeName}' uses side-effecting {unsafeReplayNode.HttpMethod} and the flow does not allow post-replay.");
            if (entry.Status == "Retrying") entry.Status = "Pending";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return new DeadLetterReplayResult
            {
                Outcome = DeadLetterReplayOutcome.PostReplayNotAllowed,
                Entry = entry
            };
        }

        var priorDeadLetters = (await _executionRepository.GetDeadLettersByExecutionAsync(entry.FlowExecutionId))
            .Select(d => d.Id)
            .ToHashSet();
        var priorTotalRecords = flowExecution.TotalRecords;
        var priorSuccessRecords = flowExecution.SuccessRecords;
        var priorFailedRecords = flowExecution.FailedRecords;
        var attemptStartedAt = DateTime.UtcNow;
        var resolved = false;

        try
        {
            _logger.LogInformation(
                "Resyncing dead letter {DeadLetterId} from node {StartNodeId} through its downstream path",
                entry.Id, startStepId);

            await _flowExecutor.ExecuteFlowFromNodeAsync(
                flow.Id,
                flowExecution.Id,
                startStepId.Value,
                string.IsNullOrWhiteSpace(entry.FlowStateJson) ? "{}" : entry.FlowStateJson,
                string.IsNullOrWhiteSpace(entry.PersistedStateJson) ? flow.PersistedStateJson : entry.PersistedStateJson,
                entry.StepId,
                cancellationToken);

            var stepExecutions = (await _executionRepository.GetStepExecutionsAsync(entry.FlowExecutionId)).ToList();
            var retriedSteps = stepExecutions
                .Where(step => step.StepId == entry.StepId && step.StartedAt >= attemptStartedAt)
                .ToList();
            var lastRetriedStep = retriedSteps.OrderByDescending(step => step.StartedAt).FirstOrDefault();
            // Resolved only when every re-run of the failed node succeeded — a single
            // successful iteration must not mask earlier failures in loop bodies.
            var failureResolved = retriedSteps.Count > 0 &&
                retriedSteps.All(step => step.Status == ExecutionStatus.Success || step.Status == ExecutionStatus.Recovered);

            var currentDeadLetters = (await _executionRepository.GetDeadLettersByExecutionAsync(entry.FlowExecutionId)).ToList();
            var newFailureCount = currentDeadLetters.Count(d => !priorDeadLetters.Contains(d.Id));
            flowExecution.TotalRecords = priorTotalRecords;
            flowExecution.SuccessRecords = priorSuccessRecords;
            flowExecution.FailedRecords = priorFailedRecords + newFailureCount;

            var completedAt = DateTime.UtcNow;
            var statusCode = lastRetriedStep != null && lastRetriedStep.HttpStatusCode > 0 ? lastRetriedStep.HttpStatusCode : (int?)null;
            if (failureResolved)
            {
                entry.Status = "Resolved";
                entry.LastRetriedAt = completedAt;
                entry.ResolvedAt = completedAt;
                AppendAttempt(entry, statusCode, string.Empty, completedAt, lastRetriedStep?.NodeName, "Replay");
                resolved = true;
            }
            else
            {
                entry.RetryCount++;
                entry.LastRetriedAt = completedAt;
                var attemptError = lastRetriedStep?.ErrorMessage
                    ?? flowExecution.ErrorMessage
                    ?? "Resync did not successfully execute the original failed node.";
                // The original failure is the root cause and is kept in ErrorMessage;
                // each replay's error is appended to the attempt history instead.
                AppendAttempt(entry, statusCode, attemptError, completedAt, lastRetriedStep?.NodeName, "Replay");
                entry.Status = entry.RetryCount >= MaxReplayAttemptsBeforeDiscard ? "Discarded" : "Pending";
            }

            await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            flowExecution.TotalRecords = priorTotalRecords;
            flowExecution.SuccessRecords = priorSuccessRecords;
            flowExecution.FailedRecords = priorFailedRecords;
            await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
            entry.RetryCount++;
            entry.LastRetriedAt = DateTime.UtcNow;
            AppendAttempt(entry, null, ex.Message, entry.LastRetriedAt.Value, null, "Replay");
            entry.Status = entry.RetryCount >= MaxReplayAttemptsBeforeDiscard ? "Discarded" : "Pending";

            _logger.LogError(ex, "Error resyncing dead letter {DeadLetterId}", entry.Id);
        }

        await _executionRepository.UpdateDeadLetterEntryAsync(entry);
        if (resolved)
        {
            await ApplyRecoveryAsync(entry, flowExecution);
        }

        return new DeadLetterReplayResult { Outcome = DeadLetterReplayOutcome.Replayed, Entry = entry };
    }

    // Resolves the node the replay actually starts at. Nodes inside a ForEach body are not
    // valid start points (the executor runs them per iteration); a request for one is moved
    // to its outermost loop, which is the only safe place to re-run the whole loop.
    private async Task<Guid?> ResolveStartNodeAsync(
        DeadLetterEntry entry,
        IntegrationFlow flow,
        Guid? requestedStartStepId)
    {
        var loopBodyNodeIds = FlowGraphAnalysis.GetAllForEachBodyNodeIds(flow);

        if (requestedStartStepId is Guid requestedId)
        {
            return await ResolveLoopSafeStartAsync(entry, flow, loopBodyNodeIds, requestedId);
        }

        if (entry.ReplayFromStepId is Guid replayFromStepId)
        {
            return await ResolveLoopSafeStartAsync(entry, flow, loopBodyNodeIds, replayFromStepId);
        }

        if (flow.DefaultDeadLetterResyncStepId is Guid configuredStartStepId)
        {
            var configured = await ResolveLoopSafeStartAsync(entry, flow, loopBodyNodeIds, configuredStartStepId);
            if (configured.HasValue)
            {
                return configured;
            }
        }

        var containingLoopId = FlowGraphAnalysis.GetOutermostForEachNodeId(flow, entry.StepId);
        if (containingLoopId is Guid loopId &&
            await IsEligibleStartNodeAsync(entry, flow, loopBodyNodeIds, loopId))
        {
            return loopId;
        }

        return await IsEligibleStartNodeAsync(entry, flow, loopBodyNodeIds, entry.StepId)
            ? entry.StepId
            : null;
    }

    private async Task<Guid?> ResolveLoopSafeStartAsync(
        DeadLetterEntry entry,
        IntegrationFlow flow,
        HashSet<Guid> loopBodyNodeIds,
        Guid startStepId)
    {
        var resolved = loopBodyNodeIds.Contains(startStepId)
            ? FlowGraphAnalysis.GetOutermostForEachNodeId(flow, startStepId)
            : startStepId;
        if (!resolved.HasValue)
        {
            return null;
        }

        return await IsEligibleStartNodeAsync(entry, flow, loopBodyNodeIds, resolved.Value)
            ? resolved
            : null;
    }

    private async Task<bool> IsEligibleStartNodeAsync(
        DeadLetterEntry entry,
        IntegrationFlow flow,
        HashSet<Guid> loopBodyNodeIds,
        Guid startStepId)
    {
        if (loopBodyNodeIds.Contains(startStepId))
        {
            return false;
        }

        var ancestors = FlowGraphAnalysis.GetAncestorNodeIds(flow, entry.StepId);
        if (!ancestors.Contains(startStepId))
        {
            return false;
        }

        if (startStepId == entry.StepId)
        {
            return true;
        }

        // The loop containing the failure is marked Failed when its body failed, but it is
        // still the right resync point because selecting it re-runs every iteration.
        if (startStepId == FlowGraphAnalysis.GetOutermostForEachNodeId(flow, entry.StepId))
        {
            return true;
        }

        var previousSteps = await _executionRepository.GetStepExecutionsAsync(entry.FlowExecutionId);
        return previousSteps.Any(step =>
            step.StepId == startStepId &&
            (step.Status == ExecutionStatus.Success || step.Status == ExecutionStatus.Recovered));
    }

    private async Task ApplyRecoveryAsync(DeadLetterEntry entry, FlowExecution flowExecution)
    {
        var recoveredAt = entry.ResolvedAt ?? DateTime.UtcNow;
        var stepExecutions = (await _executionRepository.GetStepExecutionsAsync(entry.FlowExecutionId)).ToList();
        var failedStepExecution = stepExecutions
            .Where(step => step.StepId == entry.StepId && step.Status == ExecutionStatus.Failed)
            .OrderBy(step => Math.Abs(((step.CompletedAt ?? step.StartedAt) - entry.CreatedAt).Ticks))
            .FirstOrDefault();
        if (failedStepExecution != null)
        {
            failedStepExecution.Status = ExecutionStatus.Recovered;
            failedStepExecution.RecoveredAt = recoveredAt;
            failedStepExecution.RecoveredByDeadLetterId = entry.Id;
            await _executionRepository.UpdateStepExecutionAsync(failedStepExecution);
        }

        var entries = (await _executionRepository.GetDeadLettersByExecutionAsync(entry.FlowExecutionId)).ToList();
        if (entries.All(d => d.Id != entry.Id))
        {
            entries.Add(entry);
        }

        if (entries.Any(d => d.Id != entry.Id && OutstandingStatuses.Contains(d.Status, StringComparer.OrdinalIgnoreCase)))
        {
            flowExecution.Status = ExecutionStatus.Failed;
            await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
            return;
        }

        if (flowExecution.Status == ExecutionStatus.InProgress || flowExecution.Status == ExecutionStatus.Queued)
        {
            return;
        }

        var resolvedCount = entries.Count(d => string.Equals(d.Status, "Resolved", StringComparison.OrdinalIgnoreCase));
        var moved = Math.Min(flowExecution.FailedRecords, resolvedCount);
        flowExecution.FailedRecords -= moved;
        flowExecution.SuccessRecords += moved;
        flowExecution.Status = ExecutionStatus.Recovered;
        flowExecution.RecoveredAt = recoveredAt;
        flowExecution.CompletedAt ??= recoveredAt;
        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
        _logger.LogInformation("Execution {ExecutionId} recovered after all dead letters were resolved", flowExecution.Id);
    }

    private static void AppendAttempt(DeadLetterEntry entry, int? statusCode, string error, DateTime at, string? nodeName = null, string kind = "Replay")
    {
        var history = ParseAttemptHistory(entry.AttemptHistoryJson);
        history.Add(new DeadLetterAttempt
        {
            Attempt = history.Count + 1,
            At = at,
            StatusCode = statusCode,
            Error = error,
            NodeName = nodeName ?? string.Empty,
            Kind = kind
        });
        entry.AttemptHistoryJson = JsonSerializer.Serialize(history, AttemptHistoryOptions);
    }

    private static void AppendBlockedNoteOnce(DeadLetterEntry entry, string note)
    {
        var history = ParseAttemptHistory(entry.AttemptHistoryJson);
        if (history.Any(attempt => attempt.Kind == "Blocked" && attempt.Error == note))
        {
            return;
        }
        history.Add(new DeadLetterAttempt
        {
            Attempt = history.Count + 1,
            At = DateTime.UtcNow,
            Error = note,
            Kind = "Blocked"
        });
        entry.AttemptHistoryJson = JsonSerializer.Serialize(history, AttemptHistoryOptions);
    }

    private static List<DeadLetterAttempt> ParseAttemptHistory(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<DeadLetterAttempt>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<DeadLetterAttempt>>(json, AttemptHistoryOptions) ?? new List<DeadLetterAttempt>();
        }
        catch (JsonException)
        {
            return new List<DeadLetterAttempt>();
        }
    }

    // A skipped replay must never leave the entry wedged in Retrying with no marker —
    // that state is invisible to the worker scan. Reset it to Pending so it can be
    // requested again.
    private async Task<DeadLetterReplayResult> SkipAsync(DeadLetterEntry entry)
    {
        if (entry.Status == "Retrying")
        {
            entry.Status = "Pending";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
        }
        return new DeadLetterReplayResult { Outcome = DeadLetterReplayOutcome.Skipped, Entry = entry };
    }

    private static DeadLetterReplayResult Skipped(DeadLetterEntry entry) =>
        new() { Outcome = DeadLetterReplayOutcome.Skipped, Entry = entry };

    public async Task<DeadLetterEntry?> DiscardEntryAsync(Guid entryId)
    {
        var entry = await _executionRepository.GetDeadLetterAsync(entryId);
        if (entry == null)
        {
            return null;
        }

        entry.Status = "Discarded";
        await _executionRepository.UpdateDeadLetterEntryAsync(entry);
        return entry;
    }
}
