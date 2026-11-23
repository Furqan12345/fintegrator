using System;
using System.Linq;
using System.Text.Json.Nodes;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Storage;

// The JSON sent to the cluster is what decides whether ILM can ever delete an old index, so it is
// asserted field by field.
public sealed class ElasticsearchIndexTemplateTests
{
    [Fact]
    public void IndexTemplate_CoversTheDatasetPatternAndAttachesTheIlmPolicy()
    {
        var snapshot = StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true, retentionDays: 7, granularity: StorageGranularity.Daily);

        var template = ElasticsearchIndexTemplates.BuildIndexTemplate(snapshot, StorageDatasets.Logs);

        Assert.Equal("fusetor-logs-*", template["index_patterns"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(100, template["priority"]!.GetValue<int>());

        var settings = template["template"]!["settings"]!;
        Assert.Equal(1, settings["number_of_shards"]!.GetValue<int>());
        Assert.Equal(0, settings["number_of_replicas"]!.GetValue<int>());
        Assert.Equal("fusetor-logs-ilm", settings["index.lifecycle.name"]!.GetValue<string>());
        Assert.Equal("fusetor-logs-write", settings["index.lifecycle.rollover_alias"]!.GetValue<string>());

        var meta = template["_meta"]!;
        Assert.Equal("fusetor", meta["managed_by"]!.GetValue<string>());
        Assert.Equal("logs", meta["dataset"]!.GetValue<string>());
        Assert.Equal("Daily", meta["granularity"]!.GetValue<string>());
        Assert.Equal(7, meta["retention_days"]!.GetValue<int>());
    }

    [Fact]
    public void IndexTemplate_WithoutIlm_DoesNotReferenceAPolicy()
    {
        var snapshot = StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true, useIlm: false);

        var settings = ElasticsearchIndexTemplates.BuildIndexTemplate(snapshot, StorageDatasets.Logs)["template"]!["settings"]!;

        Assert.Null(settings["index.lifecycle.name"]);
        Assert.Null(settings["index.lifecycle.rollover_alias"]);
    }

    [Theory]
    [InlineData(StorageGranularity.Daily, "1d")]
    [InlineData(StorageGranularity.Weekly, "7d")]
    [InlineData(StorageGranularity.Monthly, "30d")]
    public void IlmPolicy_RollsOverAtTheIndexPeriodAndDeletesAfterRetention(StorageGranularity granularity, string expectedRollover)
    {
        var policy = ElasticsearchIndexTemplates.BuildIlmPolicy(StorageDatasets.Logs, granularity, 30);

        var hot = policy["policy"]!["phases"]!["hot"]!;
        Assert.Equal("0ms", hot["min_age"]!.GetValue<string>());
        Assert.Equal(expectedRollover, hot["actions"]!["rollover"]!["max_age"]!.GetValue<string>());
        Assert.Equal("20gb", hot["actions"]!["rollover"]!["max_primary_shard_size"]!.GetValue<string>());

        var delete = policy["policy"]!["phases"]!["delete"]!;
        Assert.Equal("30d", delete["min_age"]!.GetValue<string>());
        Assert.NotNull(delete["actions"]!["delete"]);
    }

    [Fact]
    public void IlmPolicy_WithRetentionZero_HasNoDeletePhase()
    {
        var policy = ElasticsearchIndexTemplates.BuildIlmPolicy(StorageDatasets.CrossReferences, StorageGranularity.Monthly, 0);

        Assert.NotNull(policy["policy"]!["phases"]!["hot"]);
        Assert.Null(policy["policy"]!["phases"]!["delete"]);
    }

    [Fact]
    public void CreateIndexBody_MakesTheNewIndexTheWriteIndexOfTheAlias()
    {
        var body = ElasticsearchIndexTemplates.BuildCreateIndexBody("fusetor-logs-write");

        var alias = body["aliases"]!["fusetor-logs-write"]!;
        Assert.True(alias["is_write_index"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(StorageDatasets.Logs)]
    [InlineData(StorageDatasets.PacketLogs)]
    [InlineData(StorageDatasets.CrossReferences)]
    public void Mapping_IndexesWhatTheQueriesFilterAndNotThePayloads(string dataset)
    {
        var properties = ElasticsearchIndexTemplates.BuildMapping(dataset)["properties"]!.AsObject();

        Assert.True(properties.Count >= 7, $"{dataset} mapping looks empty");

        switch (dataset)
        {
            case StorageDatasets.Logs:
                Assert.Equal("long", properties["sequence"]!["type"]!.GetValue<string>());
                Assert.Equal("date", properties["timestamp"]!["type"]!.GetValue<string>());
                Assert.Equal("keyword", properties["level"]!["type"]!.GetValue<string>());
                Assert.Equal("text", properties["message"]!["type"]!.GetValue<string>());
                // Structured log state stays queryable.
                Assert.Equal("object", properties["properties"]!["type"]!.GetValue<string>());
                break;

            case StorageDatasets.PacketLogs:
                // Bodies are stored for the detail view but must never be indexed.
                Assert.False(properties["request_body"]!["index"]!.GetValue<bool>());
                Assert.False(properties["response_body"]!["index"]!.GetValue<bool>());
                Assert.False(properties["request_headers_json"]!["index"]!.GetValue<bool>());
                // The page filters and sorts on these.
                Assert.Equal("keyword", properties["flow_id"]!["type"]!.GetValue<string>());
                Assert.Equal("date", properties["started_at"]!["type"]!.GetValue<string>());
                Assert.Equal("keyword", properties["request_url"]!["type"]!.GetValue<string>());
                break;

            case StorageDatasets.CrossReferences:
                Assert.Equal("text", properties["key_value"]!["type"]!.GetValue<string>());
                Assert.Equal("keyword", properties["key_value"]!["fields"]!["keyword"]!["type"]!.GetValue<string>());
                Assert.Equal("keyword", properties["list_name"]!["type"]!.GetValue<string>());
                break;
        }
    }

    [Fact]
    public void CrossReferenceDocumentId_IsDeterministicPerListAndKey()
    {
        var first = ElasticsearchDocuments.CrossReferenceDocumentId("orders", "SHOP-1");
        var again = ElasticsearchDocuments.CrossReferenceDocumentId("orders", "SHOP-1");

        Assert.Equal(first, again);
        Assert.Equal(40, first.Length);
        Assert.NotEqual(first, ElasticsearchDocuments.CrossReferenceDocumentId("orders", "SHOP-2"));
        Assert.NotEqual(first, ElasticsearchDocuments.CrossReferenceDocumentId("other-list", "SHOP-1"));
    }

    [Fact]
    public void LogDocument_CarriesTheCursorAndCorrelationFields()
    {
        var entry = new Domain.Entities.AppLogEntry
        {
            Id = 42,
            EventId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Timestamp = new DateTime(2026, 10, 16, 10, 30, 0, DateTimeKind.Utc),
            Level = "Warning",
            Source = "Engine",
            Message = "card failed",
            FlowExecutionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            NodeName = "CreateBcSalesOrder",
            PropertiesJson = "{\"attempt\":2}"
        };

        var document = ElasticsearchDocuments.FromLogEntry(entry, 638000000000000000L);

        Assert.Equal(638000000000000000L, document["sequence"]!.GetValue<long>());
        Assert.Equal("2026-10-16T10:30:00.000Z", document["timestamp"]!.GetValue<string>());
        Assert.Equal("Warning", document["level"]!.GetValue<string>());
        Assert.Equal("Engine", document["source"]!.GetValue<string>());
        Assert.Equal("CreateBcSalesOrder", document["node_name"]!.GetValue<string>());
        Assert.Equal(2, document["properties"]!["attempt"]!.GetValue<int>());

        // And it survives the round trip back to the entity the UI consumes.
        var restored = ElasticsearchDocuments.ToLogEntry(document);
        Assert.Equal(638000000000000000L, restored.Id);
        Assert.Equal(entry.EventId, restored.EventId);
        Assert.Equal("Warning", restored.Level);
        Assert.Equal("card failed", restored.Message);
        Assert.Equal("CreateBcSalesOrder", restored.NodeName);
    }
}
