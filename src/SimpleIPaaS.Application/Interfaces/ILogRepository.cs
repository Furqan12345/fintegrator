using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Models;

namespace SimpleIPaaS.Application.Interfaces;

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

    Task<int> DeleteAsync(DateTime? olderThanUtc, CancellationToken cancellationToken = default);
}
