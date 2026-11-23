using System;

namespace SimpleIPaaS.Shared.Models;

public sealed class PacketLogDto
{
    public Guid PacketId { get; set; }
    public Guid FlowExecutionId { get; set; }
    public DateTime StartedAt { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public long? DurationMs { get; set; }
    public int? PageNumber { get; set; }
    public int? Attempt { get; set; }
    public string? Error { get; set; }
}

public sealed class PacketLogDetailDto
{
    public Guid PacketId { get; set; }
    public Guid FlowExecutionId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public string IntegrationName { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public int? PageNumber { get; set; }
    public int? Attempt { get; set; }
    public string? Error { get; set; }
    public string RequestHeadersJson { get; set; } = "{}";
    public string RequestBody { get; set; } = string.Empty;
    public string ResponseHeadersJson { get; set; } = "{}";
    public string ResponseBody { get; set; } = string.Empty;
}
