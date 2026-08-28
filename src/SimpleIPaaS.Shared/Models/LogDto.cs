using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class AppLogEntryDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? FlowExecutionId { get; set; }
    public Guid? FlowId { get; set; }
    public string? NodeName { get; set; }
}

public class LogPageDto
{
    public List<AppLogEntryDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }

    // Newest id in the table; hand this back to /api/logs/tail?afterId= to start tailing.
    public long Cursor { get; set; }
}

public class LogTailDto
{
    public List<AppLogEntryDto> Entries { get; set; } = new();
    public long Cursor { get; set; }

    // True when the supplied cursor was ahead of the table (logs were cleared): the client
    // should replace its buffer with Entries rather than appending to it.
    public bool Reset { get; set; }
}

public class LogDeleteResultDto
{
    public int Deleted { get; set; }
}
