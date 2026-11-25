using System;

namespace SimpleIPaaS.Domain.Entities;

public class DeadLetterEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;
    public string FlowStateJson { get; set; } = "{}";
    public string PersistedStateJson { get; set; } = "{}";
    public string ErrorMessage { get; set; } = string.Empty;
    public string AttemptHistoryJson { get; set; } = "[]";

    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // Set by the Engine after a failure-alert digest that included this entry was sent.
    public DateTime? AlertedAt { get; set; }
    public DateTime? LastRetriedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    // Set by the API when a user requests a replay; cleared by the Engine before it
    // executes the replay, giving at-most-once dispatch across process boundaries.
    public DateTime? ReplayRequestedAt { get; set; }
    public Guid? ReplayFromStepId { get; set; }

    // Never replayed automatically (DeadLetter:MaxAutoRetryAttempts is ignored), only when someone asks: set for
    // runs recovered after an Engine crash, whose flow state is rebuilt and whose last card may have half-run.
    public bool ManualReplayOnly { get; set; }

    // A Fan Out child's entry: the stored FlowStateJson holds only the top-level keys that differ from this
    // snapshot's state, and RemovedStateKeysJson the snapshot keys the child removed, so 1,000 failed children do not
    // store 1,000 copies of the shared state. The repository stores it that way and rebuilds the full state when the
    // entry is read: everything outside the repository sees the full state.
    public Guid? FanOutSnapshotId { get; set; }
    public string? RemovedStateKeysJson { get; set; }

    public string Status { get; set; } = "Pending"; // Pending, Retried, Discarded
}
