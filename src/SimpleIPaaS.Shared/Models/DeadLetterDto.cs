using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class DeadLetterDto
{
    public Guid Id { get; set; }
    public Guid FlowId { get; set; }
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string AttemptHistoryJson { get; set; } = "[]";
    public int RetryCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public bool ReplayQueued { get; set; }
    // True when a retry call found a replay already queued for this entry (idempotent retry):
    // the operator is told the request was already accepted rather than queueing a second run.
    public bool ReplayAlreadyQueued { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastRetriedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class DeadLetterAttemptDto
{
    public int Attempt { get; set; }
    public DateTime At { get; set; }
    public int? StatusCode { get; set; }
    public string Error { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string Kind { get; set; } = "Replay";
}

public class DeadLetterFailedStepDto
{
    public Guid StepExecutionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string ReceivedInput { get; set; } = string.Empty;
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
}

public class DeadLetterPacketDto
{
    public Guid PacketId { get; set; }
    public int? PageNumber { get; set; }
    public int? Attempt { get; set; }
    public string HttpMethod { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; }
}

public class DeadLetterDetailDto : DeadLetterDto
{
    public string FlowStateJson { get; set; } = "{}";
    public string PersistedStateJson { get; set; } = "{}";
    public DateTime? ReplayRequestedAt { get; set; }
    public Guid? ReplayFromStepId { get; set; }
    public string TriggerSource { get; set; } = string.Empty;
    public string TriggerPayloadJson { get; set; } = string.Empty;
    public List<DeadLetterAttemptDto> Attempts { get; set; } = new();
    public DeadLetterFailedStepDto? FailedStep { get; set; }
    public List<DeadLetterPacketDto> Packets { get; set; } = new();
}

public class ResyncDeadLetterRequestDto
{
    public Guid? StartStepId { get; set; }
    // Optional edited flow state. When supplied it must be a JSON object and replaces the
    // captured state stored on the dead letter before the replay is queued.
    public string? FlowStateJson { get; set; }
}

public sealed class BulkRetryDeadLettersRequestDto
{
    // Explicit selection. Send this or a filter, never both.
    public List<Guid>? Ids { get; set; }

    // Filter mode: at least FlowId or ExecutionId is required, so nobody can replay the
    // whole deployment by accident.
    public Guid? FlowId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? StepId { get; set; }
    public string? Search { get; set; }
    public int? Hours { get; set; }

    // Optional shared start node; only allowed when every entry belongs to one flow.
    public Guid? StartStepId { get; set; }

    // Counts what would happen without queueing anything.
    public bool DryRun { get; set; }
}

public sealed class BulkRetrySkipDto
{
    public Guid Id { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class BulkRetryDeadLettersResultDto
{
    public int Matched { get; set; }
    // Filter mode pages through matching entries in batches of 500, up to 5,000.
    // TotalMatched is how many were considered; Truncated means more remain.
    public int TotalMatched { get; set; }
    public bool Truncated { get; set; }
    public int Queued { get; set; }
    public int AlreadyQueued { get; set; }
    public int Skipped { get; set; }
    // The UI lists at most 50; Skipped still counts every skip.
    public List<BulkRetrySkipDto> Skips { get; set; } = new();
    public List<Guid> QueuedIds { get; set; } = new();
}

public class ResyncNodeOptionDto
{
    public Guid StepId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public bool IsFailedNode { get; set; }
    // True for the outermost loop containing the failure when the failed node is inside it.
    public bool ContainsFailedNode { get; set; }
    public bool IsDefault { get; set; }
}
