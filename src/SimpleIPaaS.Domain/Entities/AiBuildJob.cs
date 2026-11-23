using System;

namespace SimpleIPaaS.Domain.Entities;

public sealed class AiBuildJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public string? CurrentFlowJson { get; set; }
    // String status, not an enum: avoids the persisted-ordinal trap that StepType/ExecutionStatus have.
    public string Status { get; set; } = AiBuildJobStatus.Queued;
    public string ProgressNote { get; set; } = "Queued";
    public int ToolCallsUsed { get; set; }
    public string WarningsJson { get; set; } = "[]";
    public string SourcesJson { get; set; } = "[]";
    public string? ResultFlowJson { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class AiBuildJobStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
