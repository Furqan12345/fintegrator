using System;

namespace SimpleIPaaS.Application.Models;

// A queued FlowExecution row claimed by the Engine from the database.
// Replaces the former in-process ExecutionRequest/channel mechanism: the
// database IS the queue between the API/UI tier and the execution tier.
public record QueuedExecutionClaim(
    Guid ExecutionId,
    Guid FlowId,
    Guid TenantId,
    string TriggerSource,
    string? TriggerPayload)
{
}
