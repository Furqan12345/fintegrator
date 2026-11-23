using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Storage;

// The stores the Engine and the API actually call: what they put on the wire, and what they make of
// what comes back.
public sealed class ElasticsearchStoreTests
{

    [Fact]
    public async Task PacketLogWrite_DenormalisesTheContextThePageFiltersOn()
    {
        using var connection = new SqliteConnection("Data Source=ElasticPacketWrite;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var (connectionId, flowId, nodeId, runId, stepExecutionId) = SeedPacketContext(options, withConnection: true);

        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"took\":4,\"errors\":false,\"items\":[{\"create\":{\"_index\":\"fusetor-packet-logs-2026.10.16\",\"_id\":\"p1\",\"status\":201}}]}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(packetsOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        using var context = new IPaaSContext(options);
        var store = new ElasticPacketLogStore(clients, context);

        await store.AddAsync(new StepPacketLog
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            StepExecutionId = stepExecutionId,
            Sequence = 1,
            Kind = "HTTP",
            HttpMethod = "POST",
            RequestUrl = "https://shop.example.test/graphql.json",
            StatusCode = 200,
            RequestBody = "{\"query\":\"...\"}",
            ResponseBody = "{\"ok\":true}",
            StartedAt = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 16, 10, 0, 1, DateTimeKind.Utc),
            DurationMs = 1000
        });

        var request = handler.LastRequest;
        Assert.Equal("/fusetor-packet-logs-write/_bulk", request.Path);
        var lines = request.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var document = JsonNode.Parse(lines[1])!;

        Assert.Equal(runId.ToString(), document["flow_execution_id"]!.GetValue<string>());
        Assert.Equal(flowId.ToString(), document["flow_id"]!.GetValue<string>());
        Assert.Equal("Shopify Orders", document["flow_name"]!.GetValue<string>());
        Assert.Equal("Shopify sync", document["integration_name"]!.GetValue<string>());
        Assert.Equal("FetchShopifyOrders", document["node_name"]!.GetValue<string>());
        Assert.Equal(connectionId.ToString(), document["connection_id"]!.GetValue<string>());
        Assert.Equal("Shopify Conn", document["connection_name"]!.GetValue<string>());
        Assert.False(document["is_manual"]!.GetValue<bool>());
        Assert.Equal(200, document["status_code"]!.GetValue<int>());

        // Sizes are precomputed so the execution view never has to pull the bodies.
        Assert.Equal("{\"query\":\"...\"}".Length, document["request_body_size"]!.GetValue<int>());
        Assert.Equal("{\"ok\":true}".Length, document["response_body_size"]!.GetValue<int>());
    }

    [Fact]
    public async Task PacketLogWrite_MarksNodesWithoutAConnectionAsManual()
    {
        using var connection = new SqliteConnection("Data Source=ElasticPacketManual;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var (_, _, _, _, stepExecutionId) = SeedPacketContext(options, withConnection: false);

        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"errors\":false,\"items\":[]}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(packetsOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        using var context = new IPaaSContext(options);
        var store = new ElasticPacketLogStore(clients, context);

        await store.AddAsync(new StepPacketLog { Id = Guid.NewGuid(), StepExecutionId = stepExecutionId, Sequence = 1, Kind = "HTTP", StartedAt = DateTime.UtcNow });

        var document = JsonNode.Parse(handler.LastRequest.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1])!;
        Assert.True(document["is_manual"]!.GetValue<bool>());
        Assert.Null(document["connection_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task PacketLogPage_TranslatesFiltersTheWayTheSqlQueryDoes()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":0},\"hits\":[]}}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(packetsOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        using var connection = new SqliteConnection("Data Source=ElasticPacketPage;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;
        using var context = new IPaaSContext(options);

        var store = new ElasticPacketLogStore(clients, context);
        await store.GetPageAsync(new PacketLogPageFilter(
            FlowId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Search: "graphql",
            Outcome: "failure",
            Method: "POST",
            Hours: 24,
            Page: 2,
            PageSize: 10));

        var body = JsonNode.Parse(handler.LastRequest.Body!)!;
        var filters = body["query"]!["bool"]!["filter"]!.AsArray();

        Assert.Contains(filters, clause => clause!["term"]?["kind"]?.GetValue<string>() == "HTTP");
        Assert.Contains(filters, clause => clause!["term"]?["flow_id"]?.GetValue<string>() == "55555555-5555-5555-5555-555555555555");
        Assert.Contains(filters, clause => clause!["term"]?["http_method"]?.GetValue<string>() == "POST");
        Assert.Contains(filters, clause => clause!["query_string"]?["query"]?.GetValue<string>() == "*graphql*");
        Assert.Contains(filters, clause => clause!["range"]?["started_at"]?["gte"] != null);

        // A failure is "not a 2xx" â€” which includes packets that never got a status code at all.
        var failureClause = filters
            .Select(clause => clause!["bool"]?["must_not"])
            .FirstOrDefault(negation => negation != null)!
            .AsArray();
        Assert.Equal("200", failureClause[0]!["range"]!["status_code"]!["gte"]!.GetValue<string>());
        Assert.Equal("300", failureClause[0]!["range"]!["status_code"]!["lt"]!.GetValue<string>());

        Assert.Equal(10, body["size"]!.GetValue<int>());
        Assert.Equal(10, body["from"]!.GetValue<int>());
        Assert.Equal("desc", body["sort"]!.AsArray()[0]!["started_at"]!["order"]!.GetValue<string>());
    }

    [Fact]
    public async Task CrossReferenceUpsert_CountsOnlyGenuinelyNewKeys()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"errors\":true,\"items\":[" +
                        "{\"create\":{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"a\",\"status\":201}}," +
                        "{\"create\":{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"b\",\"status\":409,\"error\":{\"type\":\"version_conflict_engine_exception\",\"reason\":\"conflict\"}}}]}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(crossReferencesOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var store = new ElasticCrossReferenceEntryStore(clients);

        var inserted = await store.UpsertEntriesAsync("shopify-orders-imported", new[]
        {
            new CrossReferenceEntry { KeyValue = "ORDER-1", ValueJson = "{\"bc\":\"101002\"}" },
            new CrossReferenceEntry { KeyValue = "ORDER-2", ValueJson = "{}" },
            new CrossReferenceEntry { KeyValue = "ORDER-1", ValueJson = "{\"duplicate\":true}" }
        });

        Assert.Equal(1, inserted);

        var lines = handler.LastRequest.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // two unique keys, each with a document line

        var firstMeta = JsonNode.Parse(lines[0])!["create"]!;
        var expectedId = ElasticsearchDocuments.CrossReferenceDocumentId("shopify-orders-imported", "ORDER-1");
        Assert.Equal(expectedId, firstMeta["_id"]!.GetValue<string>());
        Assert.Equal("fusetor-cross-references-write", firstMeta["_index"]!.GetValue<string>());
    }

    [Fact]
    public async Task CrossReferenceExistence_UsesARealtimeMultiGet()
    {
        var handler = RecordingElasticsearchHandler.New();
        var known = ElasticsearchDocuments.CrossReferenceDocumentId("orders", "A");
        handler.Enqueue($"{{\"docs\":[{{\"_id\":\"{known}\",\"found\":true,\"_source\":{{}}}},{{\"_id\":\"missing\",\"found\":false}}]}}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(crossReferencesOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var store = new ElasticCrossReferenceEntryStore(clients);

        var existing = await store.GetExistingKeysAsync("orders", new[] { "A", "B" });

        Assert.Equal(new[] { "A" }, existing);
        Assert.Contains("_mget", handler.LastRequest.PathAndQuery);
        var ids = JsonNode.Parse(handler.LastRequest.Body!)!["ids"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(known, ids);
    }

    [Fact]
    public async Task CrossReferenceEntries_SearchAcrossKeyAndValueAndPageNewestFirst()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"count\":2}");
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_id\":\"a\",\"_source\":{\"entry_id\":\"66666666-6666-6666-6666-666666666666\",\"list_name\":\"orders\",\"key_value\":\"SHOP-1\",\"value_json\":\"{}\",\"created_at\":\"2026-10-16T09:00:00.000Z\"}}]}}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(crossReferencesOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var store = new ElasticCrossReferenceEntryStore(clients);

        var page = await store.GetEntriesAsync("orders", "SHOP", 1, 25);

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Entries);
        Assert.Equal("SHOP-1", page.Entries[0].KeyValue);
        Assert.Equal(Guid.Parse("66666666-6666-6666-6666-666666666666"), page.Entries[0].Id);

        var body = JsonNode.Parse(handler.Requests[^1].Body!)!;
        var search = body["query"]!["bool"]!["filter"]!.AsArray().First(clause => clause!["query_string"] != null);
        Assert.Equal("*SHOP*", search!["query_string"]!["query"]!.GetValue<string>());
        Assert.Equal("desc", body["sort"]!.AsArray()[0]!["created_at"]!["order"]!.GetValue<string>());
        Assert.Equal(25, body["size"]!.GetValue<int>());
    }

    [Fact]
    public async Task CrossReferenceDelete_FindsTheDocumentByEntryIdThenDeletesIt()
    {
        var entryId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":1},\"hits\":[{\"_id\":\"doc-1\",\"_source\":{}}]}}");
        handler.Enqueue("{\"errors\":false,\"items\":[{\"delete\":{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"doc-1\",\"status\":200}}]}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(crossReferencesOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var store = new ElasticCrossReferenceEntryStore(clients);

        Assert.True(await store.DeleteEntryAsync(entryId));

        Assert.Contains("/_search", handler.Requests[^2].PathAndQuery);
        Assert.Equal(entryId.ToString(), JsonNode.Parse(handler.Requests[^2].Body!)!["query"]!["bool"]!["filter"]!.AsArray()[0]!["term"]!["entry_id"]!.GetValue<string>());
        Assert.Equal("doc-1", JsonNode.Parse(handler.LastRequest.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0])!["delete"]!["_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task CrossReferenceDelete_ReturnsFalseWhenTheEntryIsGone()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":0},\"hits\":[]}}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(crossReferencesOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var store = new ElasticCrossReferenceEntryStore(clients);

        Assert.False(await store.DeleteEntryAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CrossReferenceClearAndCount_UseTheListFilter()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"count\":9}");
        handler.Enqueue("{\"deleted\":9}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(crossReferencesOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var store = new ElasticCrossReferenceEntryStore(clients);

        Assert.Equal(9, await store.CountAsync("orders"));
        Assert.Equal(9, await store.ClearListAsync("orders"));

        var countFilter = JsonNode.Parse(handler.Requests[0].Body!)!["query"]!["bool"]!["filter"]!.AsArray();
        Assert.Contains(countFilter, clause => clause!["term"]?["list_name"]?.GetValue<string>() == "orders");
    }

    [Fact]
    public async Task CrossReferenceListCounts_AreAggregatedPerList()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"hits\":{\"total\":{\"value\":0},\"hits\":[]},\"aggregations\":{\"lists\":{\"buckets\":[{\"key\":\"orders\",\"doc_count\":12},{\"key\":\"customers\",\"doc_count\":3}]}}}");
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(crossReferencesOnElasticsearch: true));
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var store = new ElasticCrossReferenceEntryStore(clients);

        var counts = await store.CountByListAsync();

        Assert.Equal(12, counts["orders"]);
        Assert.Equal(3, counts["customers"]);
        Assert.Equal(0, JsonNode.Parse(handler.LastRequest.Body!)!["size"]!.GetValue<int>());
    }

    private static (Guid ConnectionId, Guid FlowId, Guid NodeId, Guid RunId, Guid StepExecutionId) SeedPacketContext(
        DbContextOptions<IPaaSContext> options,
        bool withConnection)
    {
        var connectionId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var stepExecutionId = Guid.NewGuid();

        using var context = new IPaaSContext(options);
        context.Database.EnsureCreated();
        context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = "Shopify Orders" });

        if (withConnection)
        {
            context.Connections.Add(new Connection
            {
                Id = connectionId,
                Name = "Shopify Conn",
                BaseUrl = "https://shop.example.test",
                AuthType = AuthType.Bearer
            });
        }

        context.IntegrationSteps.Add(new IntegrationStep
        {
            Id = nodeId,
            FlowId = flowId,
            NodeName = "FetchShopifyOrders",
            StepType = StepType.HttpAction,
            ConnectionId = withConnection ? connectionId : null
        });
        context.FlowExecutions.Add(new FlowExecution
        {
            Id = runId,
            FlowId = flowId,
            FlowName = "Shopify Orders",
            IntegrationName = "Shopify sync"
        });
        context.StepExecutions.Add(new StepExecution
        {
            Id = stepExecutionId,
            FlowExecutionId = runId,
            StepId = nodeId,
            NodeName = "FetchShopifyOrders",
            Status = ExecutionStatus.Success
        });
        context.SaveChanges();

        return (connectionId, flowId, nodeId, runId, stepExecutionId);
    }
}


