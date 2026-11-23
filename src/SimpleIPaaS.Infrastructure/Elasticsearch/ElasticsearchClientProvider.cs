using System;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Hands out an ElasticsearchClient for whatever connection the storage settings currently say.
//
// One client per connection: when the operator changes the cluster URL or credentials the old
// client (and its sockets) is disposed and a new one is created, so a settings change takes effect
// without restarting either host. The cached snapshot is what the hot paths use, and it is
// non-blocking — a log write must never wait on a database read to find its sink.
public sealed class ElasticsearchClientProvider : IDisposable
{
    private readonly IStorageSettingsProvider _settings;
    private readonly object _gate = new();

    private ElasticsearchClient? _client;
    private string _fingerprint = string.Empty;
    private StorageSettingsSnapshot _snapshot = StorageSettingsSnapshot.Default;
    private bool _disposed;

    // sharedHttpClient is set by ForTests only. It must NOT be a constructor parameter: the
    // container resolves HttpClient from AddHttpClient, and that client logs every request through
    // System.Net.Http.HttpClient — which the log sink would then persist through this very client,
    // closing a feedback loop. The production client is built here, without logging handlers.
    private HttpClient? _sharedHttpClient;

    public ElasticsearchClientProvider(IStorageSettingsProvider settings)
    {
        _settings = settings;
    }

    // Test seam: lets a fake handler stand in for the cluster.
    public static ElasticsearchClientProvider ForTests(IStorageSettingsProvider settings, HttpClient httpClient)
    {
        var provider = new ElasticsearchClientProvider(settings);
        provider._sharedHttpClient = httpClient;
        return provider;
    }

    // Last snapshot this provider saw; also tells callers whether Elasticsearch is configured.
    public StorageSettingsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool IsAvailable => Snapshot.ElasticsearchEnabled && Snapshot.IsElasticsearchConfigured;

    public async Task<(ElasticsearchClient Client, StorageSettingsSnapshot Snapshot)?> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.ElasticsearchEnabled || !snapshot.IsElasticsearchConfigured)
        {
            Volatile.Write(ref _snapshot, snapshot);
            return null;
        }

        return (Resolve(snapshot), snapshot);
    }

    // Non-blocking variant for hot paths: uses the settings the provider already cached, and
    // returns false until the first load has happened.
    public bool TryGet(out ElasticsearchClient client, out StorageSettingsSnapshot snapshot)
    {
        if (!_settings.TryGetCached(out snapshot) || !snapshot.ElasticsearchEnabled || !snapshot.IsElasticsearchConfigured)
        {
            client = null!;
            return false;
        }

        client = Resolve(snapshot);
        return true;
    }

    // The client a dataset should use right now, or null when Elasticsearch is not usable. Mirrors
    // TryGet but without requiring a cached snapshot — used by the settings page and its test action.
    public ElasticsearchClient? ResolveCurrent() => TryGet(out var client, out _) ? client : null;

    // A client for an explicit snapshot that is not cached and not made current: the settings page
    // tests unsaved values through this. The caller owns the returned client; the shared HttpClient
    // (the test seam) is never disposed by it.
    public ElasticsearchClient CreateClient(StorageSettingsSnapshot snapshot) =>
        new(
            new ElasticsearchOptions(
                snapshot.Url,
                snapshot.Username,
                snapshot.Password,
                snapshot.ApiKey,
                snapshot.IndexPrefix,
                snapshot.AllowInvalidCertificate,
                snapshot.TimeoutSeconds),
            _sharedHttpClient);

    public static string Fingerprint(StorageSettingsSnapshot snapshot) =>
        $"{snapshot.Url}|{snapshot.Username}|{snapshot.ApiKey.Length}|{snapshot.Password.Length}|{snapshot.AllowInvalidCertificate}|{snapshot.TimeoutSeconds}|{snapshot.IndexPrefix}";

    private ElasticsearchClient Resolve(StorageSettingsSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        var fingerprint = Fingerprint(snapshot);

        lock (_gate)
        {
            if (_client != null && string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return _client;
            }

            _client?.Dispose();
            _client = new ElasticsearchClient(
                new ElasticsearchOptions(
                    snapshot.Url,
                    snapshot.Username,
                    snapshot.Password,
                    snapshot.ApiKey,
                    snapshot.IndexPrefix,
                    snapshot.AllowInvalidCertificate,
                    snapshot.TimeoutSeconds),
                _sharedHttpClient);
            _fingerprint = fingerprint;
            return _client;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }

        _sharedHttpClient?.Dispose();
    }
}
