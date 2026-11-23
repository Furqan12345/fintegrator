using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Elasticsearch;

namespace SimpleIPaaS.Infrastructure.Storage;

// Answers the log queries from whichever backend the logs dataset is configured for.
//
// A backend that is switched on but unreachable throws rather than falling back: the console's
// error banner ("polling paused, press Resume") is more honest than silently showing rows that
// stopped being written minutes ago.
public sealed class RoutingLogRepository : ILogRepository
{
    private readonly ILogRepository _relational;
    private readonly ElasticsearchLogRepository _elasticsearch;
    private readonly IStorageSettingsProvider _settings;

    public RoutingLogRepository(ILogRepository relational, ElasticsearchLogRepository elasticsearch, IStorageSettingsProvider settings)
    {
        _relational = relational;
        _elasticsearch = elasticsearch;
        _settings = settings;
    }

    public Task<LogPage> GetPageAsync(LogQuery query, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Current.GetPageAsync(query, page, pageSize, cancellationToken);

    public Task<LogTail> GetTailAsync(long afterId, LogQuery query, int limit, CancellationToken cancellationToken = default) =>
        Current.GetTailAsync(afterId, query, limit, cancellationToken);

    public Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
        Current.GetSourcesAsync(cancellationToken);

    public Task<LogFacets> GetFacetsAsync(LogQuery query, CancellationToken cancellationToken = default) =>
        Current.GetFacetsAsync(query, cancellationToken);

    public Task<IReadOnlyList<AppLogEntry>> GetExecutionTimelineAsync(Guid executionId, int limit, CancellationToken cancellationToken = default) =>
        Current.GetExecutionTimelineAsync(executionId, limit, cancellationToken);

    public Task<TelemetryHealthSnapshot> GetTelemetryHealthAsync(CancellationToken cancellationToken = default) =>
        Current.GetTelemetryHealthAsync(cancellationToken);

    public Task<int> DeleteAsync(DateTime? olderThanUtc, CancellationToken cancellationToken = default) =>
        Current.DeleteAsync(olderThanUtc, cancellationToken);

    private ILogRepository Current =>
        _settings.TryGetCached(out var snapshot) && snapshot.UsesElasticsearch(StorageDatasets.Logs)
            ? _elasticsearch
            : _relational;
}
