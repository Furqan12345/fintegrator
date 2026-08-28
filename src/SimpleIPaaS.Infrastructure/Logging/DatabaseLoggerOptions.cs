using System;
using Microsoft.Extensions.Logging;

namespace SimpleIPaaS.Infrastructure.Logging;

// Bound from the "Logging:Database" configuration section in both hosts.
public sealed class DatabaseLoggerOptions
{
    // Master switch. "Logging:Database:Enabled" (default true).
    public bool Enabled { get; set; } = true;

    // Provider-specific floor applied on top of the standard "Logging:LogLevel:*" rules.
    // "Logging:Database:MinimumLevel" (default Information).
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    // Retention: rows older than this are pruned. <= 0 disables age-based pruning.
    public int RetentionHours { get; set; } = 72;

    // Hard row cap; the oldest rows above the cap are pruned. <= 0 disables the cap.
    public int MaxRows { get; set; } = 100_000;

    // Flush every FlushIntervalSeconds, or immediately once BatchSize entries are queued.
    public int BatchSize { get; set; } = 200;
    public double FlushIntervalSeconds { get; set; } = 1;

    // In-memory backlog. When full the newest entries are dropped rather than blocking
    // the caller — logging must never stall a hot path.
    public int QueueCapacity { get; set; } = 20_000;

    public double PruneIntervalMinutes { get; set; } = 5;

    // Payload caps so this table can never repeat the 20MB payload problem.
    public int MaxMessageLength { get; set; } = 4_000;
    public int MaxExceptionLength { get; set; } = 16_000;

    // Extra category prefixes to suppress, on top of the non-negotiable recursion guard
    // in DatabaseLoggerProvider.AlwaysExcludedCategoryPrefixes.
    public string[] ExcludedCategoryPrefixes { get; set; } = Array.Empty<string>();

    public TimeSpan FlushInterval =>
        TimeSpan.FromSeconds(FlushIntervalSeconds <= 0 ? 1 : FlushIntervalSeconds);

    public TimeSpan PruneInterval =>
        TimeSpan.FromMinutes(PruneIntervalMinutes <= 0 ? 5 : PruneIntervalMinutes);
}
