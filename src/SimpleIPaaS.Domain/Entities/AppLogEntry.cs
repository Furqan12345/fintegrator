using System;

namespace SimpleIPaaS.Domain.Entities;

public class AppLogEntry
{
    public long Id { get; set; }
    public Guid EventId { get; set; } = Guid.NewGuid();
    public int EventVersion { get; set; } = 1;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string HostInstance { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public string ApplicationVersion { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? FlowExecutionId { get; set; }
    public Guid? FlowId { get; set; }
    public string? FlowName { get; set; }
    public Guid? IntegrationId { get; set; }
    public string? IntegrationName { get; set; }
    public Guid? ConnectionId { get; set; }
    public Guid? CardId { get; set; }
    public string? CardType { get; set; }
    public Guid? StepExecutionId { get; set; }
    public int? Invocation { get; set; }
    public string? LoopPath { get; set; }
    public int? RetryAttempt { get; set; }
    public bool? IsTest { get; set; }
    public Guid? TestCaseId { get; set; }
    public Guid? TestRunId { get; set; }
    public string? NodeName { get; set; }
    public string PropertiesJson { get; set; } = "{}";
}