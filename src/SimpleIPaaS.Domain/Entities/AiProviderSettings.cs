using System;

namespace SimpleIPaaS.Domain.Entities;

public sealed class AiProviderSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default provider";
    public bool IsDefault { get; set; }
    public string ProviderType { get; set; } = "OpenAICompatible";

    // Which provider API this row speaks: "ChatCompletions" (default â€” existing rows and
    // self-hosted OpenAI-compatible servers) or "Responses". Values live in
    // SimpleIPaaS.Shared.Models.AiApiModes; the literal here keeps Domain dependency-free.
    public string ApiMode { get; set; } = "ChatCompletions";

    // Only meaningful in Responses mode; null means the request carries no reasoning object.
    public string? ReasoningEffort { get; set; }

    // Responses mode only: adds OpenAI's built-in {type:"web_search"} tool.
    public bool EnableWebSearch { get; set; }

    // The API root (trailing /chat/completions, /responses or "/" stripped), not the request URL.
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string EncryptedApiKey { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
