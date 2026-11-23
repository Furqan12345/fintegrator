using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;


namespace SimpleIPaaS.Application.Interfaces;

public record StepExecutionSummary(
    Guid Id,
    Guid FlowExecutionId,
    Guid StepId,
    string NodeName,
    ExecutionStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    DateTime? RecoveredAt,
    Guid? RecoveredByDeadLetterId,
    int HttpStatusCode,
    string? ErrorMessage,
    int ReceivedInputSize,
    int RequestPayloadSize,
    int ResponsePayloadSize);

public record StepPacketLogSummary(
    Guid Id,
    Guid StepExecutionId,
    int Sequence,
    string Kind,
    int? PageNumber,
    int? Attempt,
    string HttpMethod,
    string RequestUrl,
    int? StatusCode,
    DateTime StartedAt,
    DateTime? CompletedAt,
    long? DurationMs,
    string? Error,
    int RequestHeadersSize,
    int RequestBodySize,
    int ResponseHeadersSize,
    int ResponseBodySize);

public record StepPayload(Guid Id, string ReceivedInput, string RequestPayload, string ResponsePayload);

public record StepPacketBody(Guid Id, string RequestHeadersJson, string RequestBody, string ResponseHeadersJson, string ResponseBody);

// Read model for the cross-run HTTP packet log page: one row per outside call with
// the flow/integration/node/connection context needed to act on it.
public record PacketLogListItem(
    Guid PacketId,
    Guid FlowExecutionId,
    DateTime StartedAt,
    string FlowName,
    string IntegrationName,
    string NodeName,
    string? ConnectionName,
    string HttpMethod,
    string RequestUrl,
    int? StatusCode,
    long? DurationMs,
    int? PageNumber,
    int? Attempt,
    string? Error);

// Full packet detail — everything sent and received, including error states.
public record PacketLogDetailItem(
    Guid PacketId,
    Guid FlowExecutionId,
    DateTime StartedAt,
    DateTime? CompletedAt,
    long? DurationMs,
    string FlowName,
    string IntegrationName,
    string NodeName,
    string? ConnectionName,
    string HttpMethod,
    string RequestUrl,
    int? StatusCode,
    int? PageNumber,
    int? Attempt,
    string? Error,
    string RequestHeadersJson,
    string RequestBody,
    string ResponseHeadersJson,
    string ResponseBody);

public record PacketLogPageFilter(
    Guid? FlowId = null,
    Guid? FlowExecutionId = null,
    Guid? IntegrationId = null,
    Guid? ConnectionId = null,
    string? Search = null,
    string? Outcome = null,
    string? Method = null,
    int? Hours = null,
    int Page = 1,
    int PageSize = 50,
    bool ManualOnly = false);

public record DeadLetterPageFilter(
    string? Status = null,
    Guid? FlowId = null,
    Guid? IntegrationId = null,
    string? Search = null,
    int? Hours = null,
    int Page = 1,
    int PageSize = 50,
    Guid? FlowExecutionId = null,
    Guid? StepId = null,
    bool IncludeTests = false);

public interface IExecutionRepository
{
    Task<FlowExecution?> GetFlowExecutionAsync(Guid id);
    // List projection: the large JSON columns (trigger payload, test definition, fan-out item, initial states)
    // are not loaded. Use GetFlowExecutionAsync for the full row.
    Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(ExecutionQuery query);

    Task AddFlowExecutionAsync(FlowExecution execution);
    Task AddFlowExecutionsAsync(IReadOnlyCollection<FlowExecution> executions);
    Task UpdateFlowExecutionAsync(FlowExecution execution);

    // Cross-process queue: the standalone Engine claims Queued rows atomically. excludedFlowIds skips flows this
    // Engine is already running (runs of one flow are serialised), so a claimed run never waits holding a slot.
    Task<QueuedExecutionClaim?> TryClaimNextQueuedExecutionAsync(IReadOnlyCollection<Guid>? excludedFlowIds = null);

    // Hands a claimed run that never started back to the queue (InProgress -> Queued), e.g. on Engine shutdown.
    Task<bool> ReleaseClaimAsync(Guid executionId);

    // Marks runs that have been InProgress since before the cutoff as Failed (their Engine died: no run can
    // legitimately last that long). Guarded per row, so several Engines can run it. Returns the runs it failed.
    // Test runs get their own cutoff: the API runs them in-process with its own limit.
    Task<IReadOnlyList<Guid>> FailStaleExecutionsAsync(DateTime startedBefore, DateTime testRunsStartedBefore, string errorMessage);
    Task<IReadOnlyList<Guid>> GetCancelledExecutionIdsAsync(IReadOnlyCollection<Guid> executionIds);

    Task AddStepExecutionAsync(StepExecution execution);
    Task UpdateStepExecutionAsync(StepExecution execution);
    Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId);
    Task<StepExecution?> GetStepExecutionAsync(Guid flowExecutionId, Guid stepId);
    Task<IReadOnlyList<StepExecutionSummary>> GetStepExecutionSummariesAsync(Guid flowExecutionId);
    Task<StepPayload?> GetStepPayloadAsync(Guid flowExecutionId, Guid stepExecutionId);
    Task AddStepPacketLogAsync(StepPacketLog packet);
    Task<IEnumerable<StepPacketLog>> GetStepPacketLogsAsync(Guid stepExecutionId);
    Task<IReadOnlyList<StepPacketLogSummary>> GetStepPacketLogSummariesAsync(Guid flowExecutionId);
    Task<StepPacketBody?> GetStepPacketBodyAsync(Guid flowExecutionId, Guid packetId);
    Task<IReadOnlyList<PacketLogListItem>> GetPacketLogPageAsync(PacketLogPageFilter filter);
    Task<PacketLogDetailItem?> GetPacketLogDetailAsync(Guid packetId);

    Task AddDeadLetterEntryAsync(DeadLetterEntry entry);
    Task<DeadLetterEntry?> GetDeadLetterAsync(Guid id);
    Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize);

    // Failure-alert scan: entries created since alerts were enabled that have not been
    // included in a digest yet. FlowId is carried for the email's "open all" link.
    Task<IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)>> GetUnalertedDeadLettersAsync(DateTime since, int max);
    Task<int> MarkDeadLettersAlertedAsync(IReadOnlyCollection<Guid> ids, DateTime at);

    // Engine-side replay scan: everything auto-retry would consider (Pending) plus
    // anything the API flagged for manual replay via ReplayRequestedAt.
    Task<IReadOnlyList<DeadLetterEntry>> GetReplayableDeadLettersAsync(int batchSize);

    Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null);
    Task<IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)>> GetDeadLetterPageAsync(DeadLetterPageFilter filter);
    Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId);
    Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry);
    // Atomic compare-and-swap claims for the dead-letter worker: exactly one caller
    // wins, so replays can never run twice or be lost to a stale read.
    Task<bool> TryClaimDeadLetterForManualReplayAsync(Guid id);
    Task<bool> TryClaimDeadLetterForAutoRetryAsync(Guid id);

    // Queues a manual replay exactly once. The row is only stamped while ReplayRequestedAt is
    // still null, so a double-click (or two operators retrying at the same moment) cannot queue
    // two replays; the loser is told the replay is already queued. A non-null flowStateJson
    // replaces the captured state in the same atomic update, so the state the operator edited
    // is exactly the state the winning replay executes.
    Task<bool> TryRequestManualReplayAsync(
        Guid id,
        Guid startStepId,
        DateTime requestedAt,
        string? flowStateJson = null);

    Task<int> ResetStaleRetryingDeadLettersAsync(TimeSpan age);
}

