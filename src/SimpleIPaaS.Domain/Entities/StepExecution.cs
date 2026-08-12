using System;

namespace SimpleIPaaS.Domain.Entities;

public class StepExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public Guid TenantId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    
    public ExecutionStatus Status { get; set; } = ExecutionStatus.InProgress;
    
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? RecoveredAt { get; set; }
    public Guid? RecoveredByDeadLetterId { get; set; }
    public string ReceivedInput { get; set; } = string.Empty;

    public int HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    
    // For large payloads, these might need to be stored externally in S3/Blob storage in a real prod system
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
}
