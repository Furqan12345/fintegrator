using System;
using System.Collections.Generic;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Models;

public sealed class LogQuery
{
    public string? MinimumLevel { get; init; }
    public string? Source { get; init; }
    public string? Service { get; init; }
    public string? Component { get; init; }
    public string? EventName { get; init; }
    public string? Operation { get; init; }
    public string? Outcome { get; init; }
    public string? Search { get; init; }
    public Guid? IntegrationId { get; init; }
    public Guid? FlowId { get; init; }
    public Guid? FlowExecutionId { get; init; }
    public Guid? CardId { get; init; }
    public Guid? StepExecutionId { get; init; }
    public string? TraceId { get; init; }
    public int? Invocation { get; init; }
    public int? RetryAttempt { get; init; }
    public bool? IsTest { get; init; }
    public Guid? TestCaseId { get; init; }
    public Guid? TestRunId { get; init; }
    public DateTime? SinceUtc { get; init; }
    public DateTime? UntilUtc { get; init; }
}

public sealed record LogPage(IReadOnlyList<AppLogEntry> Items, int Page, int PageSize, int Total, long Cursor);
public sealed record LogTail(IReadOnlyList<AppLogEntry> Entries, long Cursor, bool Reset);
public sealed record LogFacets(IReadOnlyList<string> Services, IReadOnlyList<string> Components, IReadOnlyList<string> Events, IReadOnlyList<string> Outcomes, IReadOnlyList<string> CardTypes);