using System;

namespace SimpleIPaaS.Domain.Entities;

public sealed class AiChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Role { get; set; } = AiChatMessageRole.User;
    public string Content { get; set; } = string.Empty;
    // Agent rows keep the prompt they answer here so follow-up turns chain context
    // without rereading the transcript; user rows leave it empty.
    public string Instruction { get; set; } = string.Empty;
    // String status, not an enum: avoids the persisted-ordinal trap that StepType/ExecutionStatus have.
    public string Status { get; set; } = AiChatMessageStatus.Complete;
    public string StepsJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public string SourcesJson { get; set; } = "[]";
    public string? ResultFlowJson { get; set; }
    public string Error { get; set; } = string.Empty;
    public Guid? ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public static class AiChatMessageRole
{
    public const string User = "User";
    public const string Agent = "Agent";
}

public static class AiChatMessageStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Complete = "Complete";
    public const string Failed = "Failed";
}
