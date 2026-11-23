using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Tests.TestDoubles;

// Captures every request the Elasticsearch layer makes and replays scripted responses, so the
// client, the index manager and the stores can be asserted without a cluster.
public sealed class RecordingElasticsearchHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, string, HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = new();

    public RecordedRequest LastRequest => Requests[^1];

    public RecordingElasticsearchHandler Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        return this;
    }

    public RecordingElasticsearchHandler EnqueueEmpty(HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(string.Empty, status);

    public RecordingElasticsearchHandler EnqueueNotFound(string body = "{\"error\":{\"type\":\"index_not_found_exception\",\"reason\":\"no such index\"},\"status\":404}") =>
        Enqueue(body, HttpStatusCode.NotFound);

    public RecordingElasticsearchHandler EnqueueError(string body, HttpStatusCode status) => Enqueue(body, status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri?.PathAndQuery ?? string.Empty,
            body,
            request.Headers.Authorization?.Scheme ?? string.Empty,
            request.Headers.Authorization?.Parameter ?? string.Empty,
            request.RequestUri?.Authority ?? string.Empty));

        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }

        return _responses.Dequeue()(request, body ?? string.Empty);
    }

    // A handler with no scripted responses: each test enqueues exactly one response per request it
    // expects, so a default response cannot shift the queue and hide a failure.
    public static RecordingElasticsearchHandler New() => new();
}

// Minimal service provider double: the log sink stack resolves what it needs lazily, so tests have
// to hand it something that can answer for the settings (and nothing else).
public sealed class StubServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    public StubServiceProvider(params (Type ServiceType, object Instance)[] services)
    {
        foreach (var (serviceType, instance) in services)
        {
            _services[serviceType] = instance;
        }
    }

    public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var instance) ? instance : null;
}

// Simulates an unreachable cluster.
public sealed class UnreachableElasticsearchHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
}

public sealed record RecordedRequest(string Method, string PathAndQuery, string? Body, string AuthScheme, string? AuthParameter, string Host = "")
{
    public string Path => PathAndQuery.Split('?')[0];

    public bool HasBasicAuth => string.Equals(AuthScheme, "Basic", StringComparison.OrdinalIgnoreCase);

    public bool HasApiKeyAuth => string.Equals(AuthScheme, "ApiKey", StringComparison.OrdinalIgnoreCase);
}

public sealed class StubStorageSettingsProvider : IStorageSettingsProvider
{
    public StubStorageSettingsProvider(StorageSettingsSnapshot snapshot)
    {
        Current = snapshot;
    }

    // The snapshot this stub hands out; tests flip it to simulate a settings change at runtime.
    public StorageSettingsSnapshot Current { get; set; }

    public Task<StorageSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

    public bool TryGetCached(out StorageSettingsSnapshot snapshot)
    {
        snapshot = Current;
        return true;
    }

    public void Invalidate()
    {
    }

    // A snapshot with the logs/packets/cross-references datasets routed where the test needs them.
    public static StorageSettingsSnapshot Snapshot(
        bool elasticsearchEnabled = true,
        bool logsOnElasticsearch = false,
        bool packetsOnElasticsearch = false,
        bool crossReferencesOnElasticsearch = false,
        int retentionDays = 0,
        StorageGranularity granularity = StorageGranularity.Daily,
        bool useIlm = true,
        string url = "http://localhost:9210",
        string username = "elastic",
        string password = "secret",
        string apiKey = "",
        string indexPrefix = "fusetor")
    {
        var datasets = new Dictionary<string, StorageDatasetSettings>(StringComparer.OrdinalIgnoreCase)
        {
            [StorageDatasets.Logs] = new() { Dataset = StorageDatasets.Logs, UseElasticsearch = logsOnElasticsearch, Granularity = granularity, RetentionDays = retentionDays, UseIlm = useIlm },
            [StorageDatasets.PacketLogs] = new() { Dataset = StorageDatasets.PacketLogs, UseElasticsearch = packetsOnElasticsearch, Granularity = granularity, RetentionDays = retentionDays, UseIlm = useIlm },
            [StorageDatasets.CrossReferences] = new() { Dataset = StorageDatasets.CrossReferences, UseElasticsearch = crossReferencesOnElasticsearch, Granularity = granularity, RetentionDays = retentionDays, UseIlm = useIlm },
            [StorageDatasets.Executions] = new() { Dataset = StorageDatasets.Executions, Granularity = granularity, RetentionDays = retentionDays }
        };

        return new StorageSettingsSnapshot
        {
            ElasticsearchEnabled = elasticsearchEnabled,
            Url = url,
            Username = username,
            Password = password,
            ApiKey = apiKey,
            IndexPrefix = indexPrefix,
            Datasets = datasets
        };
    }
}

// Minimal sink double for the routing writer tests.
public sealed class RecordingLogWriter : IAppLogWriter
{
    private readonly List<Domain.Entities.AppLogEntry> _entries = new();

    public RecordingLogWriter(string backend)
    {
        Backend = backend;
    }

    public string Backend { get; }

    public IReadOnlyList<Domain.Entities.AppLogEntry> Entries => _entries;

    public long DroppedCount { get; private set; }

    public long WrittenCount { get; private set; }

    public long WriteFailures { get; private set; }

    public int QueueDepth => _entries.Count;

    public DateTime? LastSuccessfulWriteUtc { get; private set; }

    public bool TryEnqueue(Domain.Entities.AppLogEntry entry)
    {
        _entries.Add(entry);
        WrittenCount++;
        LastSuccessfulWriteUtc = DateTime.UtcNow;
        return true;
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}
