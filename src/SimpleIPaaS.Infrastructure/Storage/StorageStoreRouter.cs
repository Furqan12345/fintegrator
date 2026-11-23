using System;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Storage;

// Picks the backend for one dataset from the cached storage settings.
//
// The decision is made per call rather than at startup, because the operator can move a dataset
// between backends from the settings page while both hosts are running. Reads and writes never
// cross backends silently: if a dataset is configured for Elasticsearch and the cluster is
// unreachable, the failure surfaces instead of quietly serving the other store's (now stale) data.
public sealed class StorageStoreRouter<TStore> where TStore : class
{
    private readonly TStore _relational;
    private readonly TStore _elasticsearch;
    private readonly IStorageSettingsProvider _settings;
    private readonly string _dataset;

    public StorageStoreRouter(TStore relational, TStore elasticsearch, IStorageSettingsProvider settings, string dataset)
    {
        _relational = relational;
        _elasticsearch = elasticsearch;
        _settings = settings;
        _dataset = dataset;
    }

    public TStore Current =>
        _settings.TryGetCached(out var snapshot) && snapshot.UsesElasticsearch(_dataset)
            ? _elasticsearch
            : _relational;

    public bool UsesElasticsearch =>
        _settings.TryGetCached(out var snapshot) && snapshot.UsesElasticsearch(_dataset);
}
