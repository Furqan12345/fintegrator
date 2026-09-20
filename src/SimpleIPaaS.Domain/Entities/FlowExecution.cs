using System;

namespace SimpleIPaaS.Domain.Entities;

public class FlowExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    public Guid TenantId { get; set; }
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
    public int TotalRecords { get; set; }
    public int SuccessRecords { get; set; }
    public int FailedRecords { get; set; }
}
