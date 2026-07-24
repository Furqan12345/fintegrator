namespace SimpleIPaaS.Application.Models;

public class ExecutionOptions
{
    public int MaxConcurrency { get; set; } = 4;
    public int MaxFlowDurationSeconds { get; set; } = 600;
    public int QueueCapacity { get; set; } = 100;
}
