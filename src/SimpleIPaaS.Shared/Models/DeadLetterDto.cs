using System;

namespace SimpleIPaaS.Shared.Models;

public class DeadLetterDto
{
    public Guid Id { get; set; }
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastRetriedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}
