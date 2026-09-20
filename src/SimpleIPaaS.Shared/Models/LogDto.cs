using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class AppLogEntryDto
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public int EventVersion { get; set; }
    public DateTime Timestamp { get; set; }
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

public class LogPageDto
{
    public List<AppLogEntryDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public long Cursor { get; set; }
}

public class LogTailDto
{
    public List<AppLogEntryDto> Entries { get; set; } = new();
    public long Cursor { get; set; }
    public bool Reset { get; set; }
}

public class LogDeleteResultDto
{
    public int Deleted { get; set; }
}

public sealed class LogFacetsDto
{
    public List<string> Services { get; set; } = new();
    public List<string> Components { get; set; } = new();
    public List<string> Events { get; set; } = new();
    public List<string> Outcomes { get; set; } = new();
    public List<string> CardTypes { get; set; } = new();
}

public sealed class LogTimelineDto
{
    public Guid ExecutionId { get; set; }
    public string? FlowName { get; set; }
    public string? IntegrationName { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? TraceId { get; set; }
    public List<AppLogEntryDto> Entries { get; set; } = new();
}

public sealed class TelemetryHealthDto
{
    public DateTime? LatestEventAtUtc { get; set; }
    public long StoredEventCount { get; set; }
    public long RecentErrorCount { get; set; }
    public bool DatabaseAvailable { get; set; }
    public string Status { get; set; } = "Unknown";
    public string? InstanceId { get; set; }
    public int QueueDepth { get; set; }
    public long DroppedEvents { get; set; }
    public long WriteFailures { get; set; }
    public long WrittenEvents { get; set; }
    public DateTime? LastSuccessfulWriteAtUtc { get; set; }
}