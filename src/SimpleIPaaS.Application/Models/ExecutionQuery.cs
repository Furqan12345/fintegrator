using System;
using SimpleIPaaS.Domain;

namespace SimpleIPaaS.Application.Models;

public sealed class ExecutionQuery
{
    public Guid? FlowId { get; init; }
    public Guid? IntegrationId { get; init; }
    public ExecutionStatus? Status { get; init; }

    /// <summary>Free text matched against the execution id, flow name, identifier, message, and error.</summary>
    public string? Search { get; init; }

    public DateTime? SinceUtc { get; init; }
    public DateTime? UntilUtc { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;

    /// <summary>Test/debug runs are hidden from production views unless explicitly requested.</summary>
    public bool IncludeTestRuns { get; init; }
}
