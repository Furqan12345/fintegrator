using System;
using System.Collections.Generic;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Models;

// Filter applied by both the paged log listing and the live tail.
// MinimumLevel is inclusive-and-above (Warning also returns Error and Critical).
public sealed class LogQuery
{
    public string? MinimumLevel { get; init; }
    public string? Source { get; init; }
    public string? Search { get; init; }
    public Guid? FlowExecutionId { get; init; }
    public DateTime? SinceUtc { get; init; }
}

public sealed record LogPage(IReadOnlyList<AppLogEntry> Items, int Page, int PageSize, int Total, long Cursor);

public sealed record LogTail(IReadOnlyList<AppLogEntry> Entries, long Cursor, bool Reset);
