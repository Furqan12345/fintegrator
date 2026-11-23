using System;

namespace SimpleIPaaS.Domain.Entities;

// Host-wide AI build budgets. One row, Id = 1: both the API and the Engine must see the same
// guardrails, and these are host-wide.
public sealed class AiBuildSettings
{
    public int Id { get; set; } = 1;
    public int MaxToolCalls { get; set; } = 48;
    public int MaxChatRounds { get; set; } = 60;
    public int MaxDraftRepairs { get; set; } = 4;
    public int MaxBuildMinutes { get; set; } = 30;
    public int MaxContextChars { get; set; } = 100_000;
    public int MaxNodes { get; set; } = 100;
    public int MaxEdges { get; set; } = 250;
    public int MaxHistoryTurns { get; set; } = 12;
    public int MaxHistoryCharsPerTurn { get; set; } = 4_000;
    public int MaxPromptChars { get; set; } = 48_000;
    public int MaxFlowPromptChars { get; set; } = 72_000;
    public int MaxOutputTokens { get; set; } = 32_768;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
