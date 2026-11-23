using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public sealed class AiChatSessionCreateDto
{
    public Guid? FlowId { get; set; }
    public Guid? ProviderId { get; set; }
}

public sealed class AiChatSessionSummaryDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public Guid? FlowId { get; set; }
    public int MessageCount { get; set; }
    public string LastMessagePreview { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public sealed class AiChatSessionDetailDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public Guid? FlowId { get; set; }
    public IntegrationFlowDto? DraftFlow { get; set; }
    public List<AiChatMessageDto> Messages { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AiChatMessageDto
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Role { get; set; } = "User";
    public string Content { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public string Status { get; set; } = "Complete";
    public List<AiChatStepDto> Steps { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public IntegrationFlowDto? Flow { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class AiChatStepDto
{
    public DateTime At { get; set; }
    public string Note { get; set; } = string.Empty;
    public int ToolCalls { get; set; }
}

public sealed class AiChatMessageCreateDto
{
    public string Content { get; set; } = string.Empty;
    public Guid? ProviderId { get; set; }
    public IntegrationFlowDto? CurrentFlow { get; set; }
}

public sealed class AiChatBindFlowDto
{
    public Guid FlowId { get; set; }
}
