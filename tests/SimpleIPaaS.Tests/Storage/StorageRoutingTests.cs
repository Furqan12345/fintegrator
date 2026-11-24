using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Storage;

// Routing is the feature: the same call must land in the database or in Elasticsearch depending on
// the settings page, and must follow a change without a restart.
public sealed class StorageRoutingTests
{

    [Fact]
    public void Snapshot_RoutesOnlyEnabledDatasets()
    {
        var snapshot = StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true);

        Assert.True(snapshot.UsesElasticsearch(StorageDatasets.Logs));
        Assert.False(snapshot.UsesElasticsearch(StorageDatasets.PacketLogs));
        Assert.Equal("Elasticsearch (http://localhost:9210)", snapshot.DescribeBackend(StorageDatasets.Logs));
        Assert.Equal("Database", snapshot.DescribeBackend(StorageDatasets.PacketLogs));
    }

    [Fact]
    public void Snapshot_MasterSwitchWins()
    {
        var snapshot = StubStorageSettingsProvider.Snapshot(elasticsearchEnabled: false, logsOnElasticsearch: true);

        Assert.False(snapshot.UsesElasticsearch(StorageDatasets.Logs));
        Assert.False(snapshot.IsElasticsearchConfigured == false && snapshot.ElasticsearchEnabled);
    }

    [Fact]
    public async Task LogWriter_FollowsTheDatasetBackendOnEveryEntry()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: false));
        var relational = new RecordingLogWriter("Database");
        var elasticsearch = new RecordingLogWriter("Elasticsearch");
        var services = new StubServiceProvider((typeof(IStorageSettingsProvider), settings));
        using var runtime = new StorageRuntime(services);
        await runtime.RefreshAsync();
        using var router = new RoutingLogWriter(relational, elasticsearch, runtime);

        router.TryEnqueue(NewLogEntry("first"));
        Assert.Single(relational.Entries);
        Assert.Empty(elasticsearch.Entries);
        Assert.Equal("Database", router.Backend);

        // The operator moves the logs dataset to Elasticsearch while both hosts are running.
        settings.Current = StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true);

        // The real save path calls StorageRuntime.Invalidate() and the writer follows the new
        // snapshot on the very next entry. Awaiting the refresh Invalidate started keeps this test
        // deterministic without the writer ever resolving services on the log path.
        runtime.Invalidate();
        await runtime.PendingRefresh;

        router.TryEnqueue(NewLogEntry("second"));

        Assert.Single(relational.Entries);
        Assert.Single(elasticsearch.Entries);
        Assert.Equal("Elasticsearch", router.Backend);
        Assert.Equal(2, router.WrittenCount);
    }

    [Fact]
    public async Task Catalog_SnapshotKeepsTheUsernameAndTheClientSendsBasicAuth()
    {
        // Regression: the username used to be dropped by StorageCatalog.Snapshot, so no
        // Authorization: Basic header was ever sent even though the row held credentials.
        var snapshot = StorageCatalog.Snapshot(new StorageSettings
        {
            ElasticsearchEnabled = true,
            ElasticsearchUrl = "http://localhost:9210",
            ElasticsearchUsername = "elastic",
            IndexPrefix = "fusetor"
        }) with { Password = "secret" };

        Assert.Equal("elastic", snapshot.Username);
        Assert.Equal("secret", snapshot.Password);

        var settings = new StubStorageSettingsProvider(snapshot);
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"cluster_name\":\"es-mock-cluster\",\"name\":\"es-mock\",\"version\":{\"number\":\"8.13.0\"}}");
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));

        var resolved = await clients.GetAsync();
        Assert.NotNull(resolved);
        await resolved!.Value.Client.PingAsync();

        Assert.True(handler.LastRequest.HasBasicAuth);
        Assert.Equal("elastic:secret", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastRequest.AuthParameter!)));
    }

    [Fact]
    public async Task LogRepository_FollowsTheDatasetBackend()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true));
        var relational = new RecordingLogRepository("relational");
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"count\":1}");
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_id\":\"e\",\"_source\":{\"sequence\":7,\"message\":\"from elasticsearch\"}}]}}");
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_source\":{\"sequence\":7}}]}}");
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var repository = new RoutingLogRepository(relational, new ElasticsearchLogRepository(clients), settings);

        var page = await repository.GetPageAsync(new LogQuery(), 1, 10);

        Assert.Empty(relational.Pages);
        Assert.Equal("from elasticsearch", page.Items[0].Message);
        Assert.Equal(7, page.Cursor);

        settings.Current = StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: false);
        await repository.GetPageAsync(new LogQuery(), 1, 10);

        Assert.Single(relational.Pages);
    }

    [Fact]
    public async Task LogTail_UsesTheSequenceCursorAndResetsWhenItIsAhead()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true));
        var handler = RecordingElasticsearchHandler.New();
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var repository = new ElasticsearchLogRepository(clients);

        // Normal tail: max sequence is 20, the client is at 10, two new entries come back.
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_source\":{\"sequence\":20}}]}}");       // max sequence
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":2},\"hits\":[{\"_source\":{\"sequence\":11}},{\"_source\":{\"sequence\":12}}]}}");
        var tail = await repository.GetTailAsync(10, new LogQuery(), 200);
        Assert.False(tail.Reset);
        Assert.Equal(2, tail.Entries.Count);
        Assert.Equal(12, tail.Cursor);
        Assert.Equal("fusetor-logs-*", handler.Requests[^1].Path.Replace("/_search", string.Empty).TrimStart('/'));
        var range = JsonNode.Parse(handler.Requests[^1].Body!)!["query"]!["bool"]!["filter"]!.AsArray()[1]!["range"]!["sequence"]!["gt"]!.GetValue<string>();
        Assert.Equal("10", range);

        // The client holds a cursor from before a clear: serve the newest window instead of nothing.
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_source\":{\"sequence\":5}}]}}");
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_source\":{\"sequence\":5}}]}}");
        var reset = await repository.GetTailAsync(999, new LogQuery(), 200);
        Assert.True(reset.Reset);
        Assert.Single(reset.Entries);
        Assert.Equal(5, reset.Cursor);
    }

    [Fact]
    public async Task LogPage_ReturnsTheTableWideCursorAndClampsPaging()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true));
        var handler = RecordingElasticsearchHandler.New();
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var repository = new ElasticsearchLogRepository(clients);

        handler.Enqueue("{\"count\":1200}");
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_source\":{\"sequence\":15,\"level\":\"Error\"}}]}}");
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_source\":{\"sequence\":900}}]}}");

        var page = await repository.GetPageAsync(new LogQuery { MinimumLevel = "Warning" }, 3, 900);

        Assert.Equal(1200, page.Total);
        Assert.Equal(500, page.PageSize);      // clamped like the relational repository
        Assert.Equal(900, page.Cursor);        // newest id in the whole dataset, not just this page
        var levelFilter = JsonNode.Parse(handler.Requests[0].Body!)!["query"]!["terms"]!["level"]!.AsArray();
        Assert.Equal(new[] { "Warning", "Error", "Critical" }, levelFilter.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void Catalog_SnapshotAppliesDefaultsAndClamps()
    {
        var snapshot = StorageCatalog.Snapshot(null);

        Assert.False(snapshot.ElasticsearchEnabled);
        Assert.Equal("fusetor", snapshot.IndexPrefix);
        Assert.Equal(3, snapshot.For(StorageDatasets.Logs).RetentionDays);
        Assert.Equal(StorageGranularity.Monthly, snapshot.For(StorageDatasets.CrossReferences).Granularity);
        // Production defaults; an existing installation gets its old values written down instead (StorageSettingsSeeder).
        Assert.Equal(14, snapshot.For(StorageDatasets.PacketLogs).RetentionDays);
        Assert.Equal(30, snapshot.For(StorageDatasets.Executions).RetentionDays);
        Assert.Equal(0, snapshot.For(StorageDatasets.CrossReferences).RetentionDays);

        var clamped = StorageCatalog.Parse("[{\"Dataset\":\"logs\",\"RetentionDays\":99999,\"UseElasticsearch\":true,\"Granularity\":\"Weekly\"},{\"Dataset\":\"unknown-thing\",\"RetentionDays\":5}]");

        Assert.Equal(3650, clamped.First(item => item.Dataset == StorageDatasets.Logs).RetentionDays);
        Assert.Equal(StorageGranularity.Weekly, clamped.First(item => item.Dataset == StorageDatasets.Logs).Granularity);
        Assert.DoesNotContain(clamped, item => item.Dataset == "unknown-thing");
        Assert.Equal(4, clamped.Count);
    }

    [Fact]
    public void Catalog_ExecutionHistoryStaysInTheDatabase()
    {
        var definition = StorageCatalog.Definition(StorageDatasets.Executions);

        Assert.False(definition.CanUseElasticsearch);
        Assert.Contains("atomic", definition.VolumeHint, StringComparison.OrdinalIgnoreCase);

        // Even a hand-edited row cannot move it.
        var parsed = StorageCatalog.Parse("[{\"Dataset\":\"executions\",\"UseElasticsearch\":true,\"RetentionDays\":30}]");
        var executions = parsed.First(item => item.Dataset == StorageDatasets.Executions);
        Assert.False(executions.UseElasticsearch);
        Assert.Equal(30, executions.RetentionDays);
    }

    [Fact]
    public void Catalog_PrefixIsSanitisedIntoALegalIndexName()
    {
        Assert.Equal("acme-prod", StorageCatalog.SanitizePrefix("  ACME Prod  "));
        Assert.Equal("fusetor", StorageCatalog.SanitizePrefix("***"));
        Assert.Equal("fusetor", StorageCatalog.SanitizePrefix(null));
    }

    private static AppLogEntry NewLogEntry(string message) => new()
    {
        EventId = Guid.NewGuid(),
        Timestamp = DateTime.UtcNow,
        Level = "Information",
        Source = "Api",
        Category = "Test",
        Message = message
    };

    private sealed class RecordingLogRepository : ILogRepository
    {
        public RecordingLogRepository(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public List<LogQuery> Pages { get; } = new();

        public Task<LogPage> GetPageAsync(LogQuery query, int page, int pageSize, System.Threading.CancellationToken cancellationToken = default)
        {
            Pages.Add(query);
            return Task.FromResult(new LogPage(Array.Empty<AppLogEntry>(), page, pageSize, 0, 0));
        }

        public Task<LogTail> GetTailAsync(long afterId, LogQuery query, int limit, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogTail(Array.Empty<AppLogEntry>(), afterId, false));

        public Task<IReadOnlyList<string>> GetSourcesAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

        public Task<LogFacets> GetFacetsAsync(LogQuery query, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogFacets(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));

        public Task<IReadOnlyList<AppLogEntry>> GetExecutionTimelineAsync(Guid executionId, int limit, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<AppLogEntry>)Array.Empty<AppLogEntry>());

        public Task<TelemetryHealthSnapshot> GetTelemetryHealthAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelemetryHealthSnapshot(null, 0, 0));

        public Task<int> DeleteAsync(DateTime? olderThanUtc, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}

