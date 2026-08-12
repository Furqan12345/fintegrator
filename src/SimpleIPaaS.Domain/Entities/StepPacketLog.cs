using System;

namespace SimpleIPaaS.Domain.Entities;

public class StepPacketLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StepExecutionId { get; set; }
    public Guid TenantId { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public int? Attempt { get; set; }
    public string HttpMethod { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string RequestHeadersJson { get; set; } = "{}";
    public string RequestBody { get; set; } = string.Empty;
    public string ResponseHeadersJson { get; set; } = "{}";
    public string ResponseBody { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
}
