using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleIPaaS.Application.Interfaces;

public sealed record LogVolumeSource(string Name, long Lines);

// How much the relational log table holds. StoredBytes is known on PostgreSQL only. The breakdown (what wrote the
// most in the last 24 hours) is filled only when asked for, because it groups a day of rows.
public sealed record LogVolume(
    long StoredLines,
    long? StoredBytes,
    long LastDayLines,
    IReadOnlyList<LogVolumeSource> TopCategories,
    IReadOnlyList<LogVolumeSource> TopFlows);

// Measures the relational AppLogEntries table (not an Elasticsearch logs index, which has its own lifecycle).
public interface ILogVolumeProbe
{
    Task<LogVolume> MeasureAsync(bool includeBreakdown, CancellationToken cancellationToken = default);
}
