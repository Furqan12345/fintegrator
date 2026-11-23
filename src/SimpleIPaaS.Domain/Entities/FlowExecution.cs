using System;

namespace SimpleIPaaS.Domain.Entities;

public class FlowExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public Guid? IntegrationId { get; set; }
    public string IntegrationName { get; set; } = string.Empty;
    public string? TraceParent { get; set; }

    public ExecutionStatus Status { get; set; } = ExecutionStatus.InProgress;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? QueuedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? RecoveredAt { get; set; }

    public string? ErrorMessage { get; set; }
    public string TriggerSource { get; set; } = string.Empty;
    public string? TriggerPayloadJson { get; set; }
    public bool IsTest { get; set; }
    public Guid? TestCaseId { get; set; }
    public Guid? TestRunId { get; set; }
    public string? TestDefinitionJson { get; set; }
    public Guid? ParentExecutionId { get; set; }
    public Guid? FanOutNodeId { get; set; }
    public string FanOutNodeName { get; set; } = string.Empty;
    public int? FanOutItemIndex { get; set; }
    public int? FanOutItemCount { get; set; }
    public string? FanOutItemJson { get; set; }
    public string? StartStepIdsJson { get; set; }
    public string? InitialFlowStateJson { get; set; }
    public string? InitialPersistedStateJson { get; set; }
    public int TotalRecords { get; set; }
    public int SuccessRecords { get; set; }
    public int FailedRecords { get; set; }

    // Set by AddIdentifier / Error / Success cards: short strings surfaced as
    // columns on the transaction page.
    public string? Identifier { get; set; }
    public string? Message { get; set; }
}
