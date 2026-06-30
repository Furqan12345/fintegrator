using System;

namespace SimpleIPaaS.Domain.Entities;

public class DeadLetterEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public Guid TenantId { get; set; }
    
    public string Payload { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRetriedAt { get; set; }
    
    public string Status { get; set; } = "Pending"; // Pending, Retried, Discarded
}
