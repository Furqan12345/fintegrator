using System;

namespace SimpleIPaaS.Domain.Entities;

public class FlowExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    public Guid TenantId { get; set; }
    
    public ExecutionStatus Status { get; set; } = ExecutionStatus.InProgress;
    
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    
    public string? ErrorMessage { get; set; }
    public int TotalRecords { get; set; }
    public int SuccessRecords { get; set; }
    public int FailedRecords { get; set; }
}
