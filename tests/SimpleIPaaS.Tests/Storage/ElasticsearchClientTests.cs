using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Storage;

// Request-shape tests: paths, bodies and auth as the wire sees them. A real cluster also needs to
// accept these, which is what the mock cluster in tools/FintegratorStorage verifies end to end.
public sealed class ElasticsearchClientTests
{
    private static ElasticsearchOptions Options(string apiKey = "") => new(
        "http://localhost:9210/", "elastic", "secret", apiKey, "fusetor", false, 15);

    [Fact]
    public async Task Ping_ReadsClusterIdentity()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"name\":\"es-mock\",\"cluster_name\":\"es-mock-cluster\",\"version\":{\"number\":\"8.13.0\",\"build_flavor\":\"default\"}}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        var info = await client.PingAsync();

        Assert.Equal("es-mock", info.NodeName);
        Assert.Equal("es-mock-cluster", info.ClusterName);
        Assert.Equal("8.13.0", info.Version);
        Assert.Equal("GET", handler.LastRequest.Method);
        Assert.Equal("/", handler.LastRequest.Path);
    }

    [Fact]
    public async Task CreateIndex_PutsTheAliasBodyAndCreatesWithoutQueryNoise()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"acknowledged\":true,\"index\":\"fusetor-logs-2026.10.16\"}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        await client.CreateIndexAsync("fusetor-logs-2026.10.16", ElasticsearchIndexTemplates.BuildCreateIndexBody("fusetor-logs-write"));

        var request = handler.LastRequest;
        Assert.Equal("PUT", request.Method);
        Assert.Equal("/fusetor-logs-2026.10.16", request.Path);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.True(body["aliases"]!["fusetor-logs-write"]!["is_write_index"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TemplateAndPolicy_UseTheDocumentedEndpoints()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"acknowledged\":true}");
        handler.Enqueue("{\"acknowledged\":true}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        await client.PutIndexTemplateAsync("fusetor-logs-template", new JsonObject { ["index_patterns"] = new JsonArray("fusetor-logs-*") });
        await client.PutIlmPolicyAsync("fusetor-logs-ilm", new JsonObject { ["policy"] = new JsonObject() });

        Assert.Equal("/_index_template/fusetor-logs-template", handler.Requests[^2].Path);
        Assert.Equal("PUT", handler.Requests[^2].Method);
        Assert.Equal("/_ilm/policy/fusetor-logs-ilm", handler.Requests[^1].Path);
        Assert.Equal("PUT", handler.Requests[^1].Method);
    }

    [Fact]
    public async Task AliasUpdate_SendsTheActionsArray()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"acknowledged\":true}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        await client.UpdateAliasesAsync(new JsonArray
        {
            new JsonObject
            {
                ["add"] = new JsonObject { ["index"] = "fusetor-logs-2026.10.16", ["alias"] = "fusetor-logs-write", ["is_write_index"] = true }
            }
        });

        Assert.Equal("/_aliases", handler.LastRequest.Path);
        Assert.Equal("POST", handler.LastRequest.Method);
        Assert.Equal("fusetor-logs-write", JsonNode.Parse(handler.LastRequest.Body!)!["actions"]!.AsArray()[0]!["add"]!["alias"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bulk_SendsNdjsonAndReportsPerItemOutcomes()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"took\":5,\"errors\":true,\"items\":[" +
                        "{\"create\":{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"a\",\"status\":201}}," +
                        "{\"create\":{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"b\",\"status\":409,\"error\":{\"type\":\"version_conflict_engine_exception\",\"reason\":\"[b]: version conflict\"}}}," +
                        "{\"delete\":{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"c\",\"status\":200}}]}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        var result = await client.BulkAsync("fusetor-cross-references-write", new[]
        {
            ElasticsearchBulkOperation.Create("a", new JsonObject { ["key_value"] = "1" }),
            ElasticsearchBulkOperation.Create("b", new JsonObject { ["key_value"] = "2" }),
            ElasticsearchBulkOperation.Delete("c")
        });

        var lines = handler.LastRequest.Body!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length); // create+doc, create+doc, delete (no doc line)
        Assert.Equal("c", JsonNode.Parse(lines[4])!["delete"]!["_id"]!.GetValue<string>());
        Assert.Equal("fusetor-cross-references-write", JsonNode.Parse(lines[0])!["create"]!["_index"]!.GetValue<string>());

        Assert.True(result.Errors);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public async Task Search_ParsesTotalsHitsAndAggregations()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"took\":3,\"hits\":{\"total\":{\"value\":17,\"relation\":\"eq\"},\"hits\":[{\"_index\":\"fusetor-logs-2026.10.16\",\"_id\":\"e1\",\"_source\":{\"sequence\":99,\"level\":\"Error\"}}]},\"aggregations\":{\"services\":{\"buckets\":[{\"key\":\"Api\",\"doc_count\":3}]}}}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        var result = await client.SearchAsync("fusetor-logs-*", new JsonObject { ["size"] = 1 });

        Assert.Equal(17, result.Total);
        Assert.Single(result.Hits);
        Assert.Equal("e1", result.Hits[0].Id);
        Assert.Equal(99L, result.Hits[0].Source!["sequence"]!.GetValue<long>());
        Assert.Equal("Api", result.Aggregations!["services"]!["buckets"]!.AsArray()[0]!["key"]!.GetValue<string>());

        // Searches must survive a dataset that has no index yet.
        Assert.Contains("ignore_unavailable=true", handler.LastRequest.PathAndQuery);
        Assert.Contains("allow_no_indices=true", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task ListIndices_ReadsTheCatColumnsAndToleratesAMissingPattern()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("[{\"index\":\"fusetor-logs-2026.10.16\",\"health\":\"green\",\"status\":\"open\",\"docs.count\":42,\"store.size\":1024,\"creation.date\":1770000000000}]");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        var indices = await client.ListIndicesAsync("fusetor-logs-*");
        Assert.Single(indices);
        Assert.Equal(42, indices[0].DocumentCount);
        Assert.Equal("1024", indices[0].Size);
        Assert.NotNull(indices[0].CreatedAtUtc);
        Assert.Contains("format=json", handler.LastRequest.PathAndQuery);

        handler.EnqueueNotFound("{}");
        Assert.Empty(await client.ListIndicesAsync("fusetor-nothing-*"));
    }

    [Fact]
    public async Task DeleteIndex_ToleratesAMissingIndexButReportsOtherErrors()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.EnqueueNotFound("{}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        // The other host's retention pass may already have removed it.
        await client.DeleteIndexAsync("fusetor-logs-2026.09.01");

        handler.EnqueueError("{\"error\":{\"type\":\"security_exception\",\"reason\":\"action [delete] is unauthorized\"},\"status\":403}", HttpStatusCode.Forbidden);
        var exception = await Assert.ThrowsAsync<ElasticsearchRequestException>(() => client.DeleteIndexAsync("fusetor-logs-2026.09.02"));
        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("security_exception", exception.ErrorType);
        Assert.Contains("unauthorized", exception.Reason);
    }

    [Fact]
    public async Task CountAndDeleteByQuery_UseTheDatasetPattern()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"count\":1204}");
        handler.Enqueue("{\"deleted\":7}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        var count = await client.CountAsync("fusetor-logs-*", ElasticsearchQueryBuilder.Term("level", "Error"));
        var deleted = await client.DeleteByQueryAsync("fusetor-logs-*", ElasticsearchQueryBuilder.MatchAll(), refresh: true);

        Assert.Equal(1204, count);
        Assert.Equal(7, deleted);
        Assert.Contains("/fusetor-logs-*/_count", handler.Requests[^2].PathAndQuery);
        Assert.Contains("refresh=true", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task MultiGet_ReportsFoundAndMissingDocuments()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"docs\":[{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"a\",\"found\":true,\"_source\":{\"key_value\":\"A\"}},{\"_index\":\"fusetor-cross-references-2026.10\",\"_id\":\"b\",\"found\":false}]}");
        using var client = new ElasticsearchClient(Options(), new HttpClient(handler));

        var found = await client.MultiGetAsync("fusetor-cross-references-*", new[] { "a", "b" });

        Assert.True(found.ContainsKey("a"));
        Assert.NotNull(found["a"]);
        Assert.Null(found["b"]);
        Assert.Contains("_mget", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task Authentication_UsesBasicOrApiKey()
    {
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"count\":0}");
        using var basic = new ElasticsearchClient(Options(), new HttpClient(handler));
        await basic.CountAsync("fusetor-logs-*", null);

        Assert.True(handler.LastRequest.HasBasicAuth);
        Assert.False(handler.LastRequest.HasApiKeyAuth);

        handler.Enqueue("{\"count\":0}");
        using var apiKey = new ElasticsearchClient(Options("base64-key"), new HttpClient(handler));
        await apiKey.CountAsync("fusetor-logs-*", null);

        Assert.True(handler.LastRequest.HasApiKeyAuth);
    }

    [Fact]
    public async Task AConnectionFailure_SurfacesAsARequestException()
    {
        using var client = new ElasticsearchClient(
            new ElasticsearchOptions("http://127.0.0.1:9210", string.Empty, string.Empty, string.Empty, "fusetor", false, 1),
            new HttpClient(new UnreachableElasticsearchHandler()));

        var exception = await Assert.ThrowsAsync<ElasticsearchRequestException>(() => client.PingAsync());
        Assert.Equal(0, exception.StatusCode);
        Assert.Equal("connection_failed", exception.ErrorType);
        Assert.Contains("refused", exception.Reason);
    }

    [Fact]
    public async Task AnUnconfiguredCluster_IsRejectedBeforeAnyRequest()
    {
        var handler = RecordingElasticsearchHandler.New();
        using var client = new ElasticsearchClient(ElasticsearchOptions.Disabled, new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void QueryBuilder_MirrorsTheSqlFilters()
    {
        var search = ElasticsearchQueryBuilder.SearchAcross(new[] { "message", "node_name" }, "order");
        Assert.Equal("*order*", search["query_string"]!["query"]!.GetValue<string>());
        Assert.True(search["query_string"]!["analyze_wildcard"]!.GetValue<bool>());

        var range = ElasticsearchQueryBuilder.Range("started_at", gte: "2026-10-16T00:00:00.000Z");
        Assert.Equal("2026-10-16T00:00:00.000Z", range["range"]!["started_at"]!["gte"]!.GetValue<string>());

        var combined = ElasticsearchQueryBuilder.Bool(filter: new[] { ElasticsearchQueryBuilder.Term("kind", "HTTP"), range }, mustNot: new[] { ElasticsearchQueryBuilder.Exists("error") });
        Assert.Equal(2, combined["bool"]!["filter"]!.AsArray().Count);
        Assert.Single(combined["bool"]!["must_not"]!.AsArray());
    }
}
