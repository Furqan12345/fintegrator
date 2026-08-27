using System;

namespace SimpleIPaaS.Domain.Entities;

public class DeadLetterEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public Guid TenantId { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;
    public string FlowStateJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;
    public string AttemptHistoryJson { get; set; } = "[]";

    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRetriedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    // Set by the API when a user requests a replay; cleared by the Engine before it
    // executes the replay, giving at-most-once dispatch across process boundaries.
    public DateTime? ReplayRequestedAt { get; set; }

    public string Status { get; set; } = "Pending"; // Pending, Retried, Discarded
}
