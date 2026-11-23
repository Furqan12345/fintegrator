using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public sealed class AiProviderSettingsDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Default provider";
    public bool IsDefault { get; set; }
    public string ProviderType { get; set; } = "OpenAICompatible";
    public string ApiMode { get; set; } = AiApiModes.ChatCompletions;
    public string? ReasoningEffort { get; set; }
    public bool EnableWebSearch { get; set; }

    // The normalized API root, e.g. https://api.openai.com/v1 — the app appends
    // /chat/completions or /responses at request time.
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class AiProviderSettingsUpdateDto
{
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public string ProviderType { get; set; } = "OpenAICompatible";

    // Blank on create = let the server pick the default for the provider type; blank on
    // update = keep the stored mode.
    public string ApiMode { get; set; } = string.Empty;
    public string? ReasoningEffort { get; set; }

    // Null on update = keep the stored flag (this DTO is also posted by older clients).
    public bool? EnableWebSearch { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool ClearApiKey { get; set; }
}

public sealed class AiFlowBuildRequestDto
{
    public Guid? ProviderId { get; set; }
    public string Instruction { get; set; } = string.Empty;
    public IntegrationFlowDto? CurrentFlow { get; set; }
}

public sealed class AiFlowBuildResponseDto
{
    public IntegrationFlowDto Flow { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public bool IsConversational { get; set; }
}

public sealed class AiProviderTestResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class AiBuildJobCreateDto
{
    public Guid? ProviderId { get; set; }
    public string Instruction { get; set; } = string.Empty;
    public IntegrationFlowDto? CurrentFlow { get; set; }
}

public sealed class AiBuildJobDto
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public string ProgressNote { get; set; } = string.Empty;
    public int ToolCallsUsed { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public IntegrationFlowDto? Flow { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
