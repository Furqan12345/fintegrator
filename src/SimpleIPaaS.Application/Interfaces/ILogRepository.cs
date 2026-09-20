using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public sealed record TelemetryHealthSnapshot(DateTime? LatestEventAtUtc, long StoredEventCount, long RecentErrorCount);

// Read/maintenance side of the AppLogEntries table. Writing is the exclusive job of
// SimpleIPaaS.Infrastructure.Logging.DatabaseLogWriter, which bypasses EF entirely.
public interface ILogRepository
{
    Task<LogPage> GetPageAsync(LogQuery query, int page, int pageSize, CancellationToken cancellationToken = default);

    // Live tail: everything with Id > afterId, oldest-first, capped at limit.
    // Sets Reset when afterId is ahead of the table (logs cleared, or a fresh cursor),
    // in which case the caller should treat the returned entries as a full refresh.
    Task<LogTail> GetTailAsync(long afterId, LogQuery query, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default);
    Task<LogFacets> GetFacetsAsync(LogQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AppLogEntry>> GetExecutionTimelineAsync(Guid executionId, int limit, CancellationToken cancellationToken = default);
    Task<TelemetryHealthSnapshot> GetTelemetryHealthAsync(CancellationToken cancellationToken = default);

    Task<int> DeleteAsync(DateTime? olderThanUtc, CancellationToken cancellationToken = default);
}
