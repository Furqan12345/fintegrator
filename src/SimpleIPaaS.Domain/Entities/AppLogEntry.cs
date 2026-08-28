using System;

namespace SimpleIPaaS.Domain.Entities;

// A single application log record captured from one of the hosts (API or Engine) and
// persisted into the shared database so the UI can tail it. Deliberately flat and
// append-only: rows are written by the batched DatabaseLoggerProvider and pruned by
// retention, never updated.
public class AppLogEntry
{
    // Monotonic INTEGER PRIMARY KEY AUTOINCREMENT — doubles as the live-tail cursor.
    public long Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // LogLevel name: Trace/Debug/Information/Warning/Error/Critical.
    public string Level { get; set; } = string.Empty;

    // Which host emitted the entry: "Engine" or "Api".
    public string Source { get; set; } = string.Empty;

    // Logger category name (usually the fully-qualified type).
    public string Category { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    // Full exception text (ToString), truncated to the configured cap. Null when none.
    public string? Exception { get; set; }

    // Correlation columns, harvested from ILogger scopes/structured state when present.
    public Guid? TenantId { get; set; }
    public Guid? FlowExecutionId { get; set; }
    public Guid? FlowId { get; set; }
    public string? NodeName { get; set; }
}
