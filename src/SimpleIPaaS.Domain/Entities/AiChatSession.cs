using System;

namespace SimpleIPaaS.Domain.Entities;

public sealed class AiChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New build session";
    public Guid? FlowId { get; set; }
    // String status, not an enum: avoids the persisted-ordinal trap that StepType/ExecutionStatus have.
    public string Status { get; set; } = AiChatSessionStatus.Active;
    public string? DraftFlowJson { get; set; }
    public Guid? LastProviderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class AiChatSessionStatus
{
    public const string Active = "Active";
    public const string Ended = "Ended";
}
