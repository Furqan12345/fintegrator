using System;

namespace SimpleIPaaS.Application.Models;

public record ExecutionRequest
{
    public Guid ExecutionId { get; init; }
    public Guid FlowId { get; init; }
    public Guid TenantId { get; init; }
    public string TriggerSource { get; init; } = string.Empty;
    public string? TriggerPayload { get; init; }
}
