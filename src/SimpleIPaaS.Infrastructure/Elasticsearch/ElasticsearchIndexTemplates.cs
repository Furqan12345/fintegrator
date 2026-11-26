using System;
using System.Text.Json.Nodes;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Index templates, mappings and ILM policies per dataset. Pure functions on purpose: the JSON the
// operator's cluster will receive is exactly what the tests assert, and the settings page shows
// the same names it will create.
public static class ElasticsearchIndexTemplates
{
    public const int Shards = 1;

    // Single node clusters (and the mock used in tests) cannot allocate replicas; a second copy
    // is a cluster decision, not an application one.
    public const int Replicas = 0;

    public static JsonObject BuildIndexTemplate(StorageSettingsSnapshot settings, string dataset)
    {
        var definition = StorageCatalog.Definition(dataset);
        var datasetSettings = settings.For(dataset);
        var pattern = StorageIndexNaming.Pattern(settings.IndexPrefix, dataset);
        var writeAlias = StorageIndexNaming.WriteAlias(settings.IndexPrefix, dataset);
        var templateSettings = new JsonObject
        {
            ["number_of_shards"] = Shards,
            ["number_of_replicas"] = Replicas,
            ["index.mapping.total_fields.limit"] = 2000
        };

        if (datasetSettings.UseIlm && definition.CanUseIlm)
        {
            // Attaching the policy through the template means every rolled-over index inherits
            // it, including ones ILM creates on its own.
            templateSettings["index.lifecycle.name"] = StorageIndexNaming.PolicyName(settings.IndexPrefix, dataset);
            templateSettings["index.lifecycle.rollover_alias"] = writeAlias;
        }

        return new JsonObject
        {
            ["index_patterns"] = new JsonArray(pattern),
            ["priority"] = 100,
            ["_meta"] = new JsonObject
            {
                ["managed_by"] = "fusetor",
                ["dataset"] = dataset,
                ["granularity"] = datasetSettings.Granularity.ToString(),
                ["retention_days"] = StorageCatalog.IndexRetentionDays(datasetSettings),
                ["write_alias"] = writeAlias
            },
            ["template"] = new JsonObject
            {
                ["settings"] = templateSettings,
                ["mappings"] = BuildMapping(dataset)
            }
        };
    }

    public static JsonObject BuildIlmPolicy(string dataset, StorageGranularity granularity, int retentionDays)
    {
        var phases = new JsonObject
        {
            ["hot"] = new JsonObject
            {
                ["min_age"] = "0ms",
                ["actions"] = new JsonObject
                {
                    // Rolling over on the same period length as the index name keeps the number of
                    // documents per shard bounded even when a dataset gets far busier than planned.
                    ["rollover"] = new JsonObject
                    {
                        ["max_age"] = RolloverAge(granularity),
                        ["max_primary_shard_size"] = "20gb"
                    }
                }
            }
        };

        if (retentionDays > 0)
        {
            phases["delete"] = new JsonObject
            {
                ["min_age"] = $"{retentionDays}d",
                ["actions"] = new JsonObject { ["delete"] = new JsonObject() }
            };
        }

        return new JsonObject { ["policy"] = new JsonObject { ["phases"] = phases } };
    }

    public static string RolloverAge(StorageGranularity granularity) => granularity switch
    {
        StorageGranularity.Monthly => "30d",
        StorageGranularity.Weekly => "7d",
        _ => "1d"
    };

    // Bootstrapping the first backing index: created with the alias already pointing at it as the
    // write index, so writers never have to know the period in the name.
    public static JsonObject BuildCreateIndexBody(string writeAlias) => new()
    {
        ["aliases"] = new JsonObject
        {
            [writeAlias] = new JsonObject { ["is_write_index"] = true }
        }
    };

    public static JsonObject BuildMapping(string dataset) => dataset switch
    {
        StorageDatasets.PacketLogs => PacketLogMapping(),
        StorageDatasets.CrossReferences => CrossReferenceMapping(),
        _ => LogMapping()
    };

    // Log documents. Keyword for everything the console filters on, text for what it searches,
    // and "properties" stays a dynamic object so structured log state can be queried later.
    private static JsonObject LogMapping() => new()
    {
        ["dynamic"] = true,
        ["properties"] = new JsonObject
        {
            ["sequence"] = new JsonObject { ["type"] = "long" },
            ["event_id"] = new JsonObject { ["type"] = "keyword" },
            ["event_version"] = new JsonObject { ["type"] = "integer" },
            ["timestamp"] = new JsonObject { ["type"] = "date" },
            ["level"] = new JsonObject { ["type"] = "keyword" },
            ["source"] = new JsonObject { ["type"] = "keyword" },
            ["service"] = new JsonObject { ["type"] = "keyword" },
            ["host_instance"] = new JsonObject { ["type"] = "keyword" },
            ["environment"] = new JsonObject { ["type"] = "keyword" },
            ["application_version"] = new JsonObject { ["type"] = "keyword" },
            ["category"] = new JsonObject { ["type"] = "keyword" },
            ["component"] = new JsonObject { ["type"] = "keyword" },
            ["event_name"] = new JsonObject { ["type"] = "keyword" },
            ["operation"] = new JsonObject { ["type"] = "keyword" },
            ["outcome"] = new JsonObject { ["type"] = "keyword" },
            ["duration_ms"] = new JsonObject { ["type"] = "long" },
            ["trace_id"] = new JsonObject { ["type"] = "keyword" },
            ["span_id"] = new JsonObject { ["type"] = "keyword" },
            ["parent_span_id"] = new JsonObject { ["type"] = "keyword" },
            ["message"] = new JsonObject { ["type"] = "text" },
            ["exception"] = new JsonObject { ["type"] = "text" },
            ["flow_execution_id"] = new JsonObject { ["type"] = "keyword" },
            ["flow_id"] = new JsonObject { ["type"] = "keyword" },
            ["flow_name"] = new JsonObject { ["type"] = "keyword" },
            ["integration_id"] = new JsonObject { ["type"] = "keyword" },
            ["integration_name"] = new JsonObject { ["type"] = "keyword" },
            ["connection_id"] = new JsonObject { ["type"] = "keyword" },
            ["card_id"] = new JsonObject { ["type"] = "keyword" },
            ["card_type"] = new JsonObject { ["type"] = "keyword" },
            ["step_execution_id"] = new JsonObject { ["type"] = "keyword" },
            ["invocation"] = new JsonObject { ["type"] = "integer" },
            ["loop_path"] = new JsonObject { ["type"] = "keyword" },
            ["retry_attempt"] = new JsonObject { ["type"] = "integer" },
            ["is_test"] = new JsonObject { ["type"] = "boolean" },
            ["test_case_id"] = new JsonObject { ["type"] = "keyword" },
            ["test_run_id"] = new JsonObject { ["type"] = "keyword" },
            ["node_name"] = new JsonObject { ["type"] = "keyword" },
            ["properties"] = new JsonObject { ["type"] = "object", ["dynamic"] = true }
        }
    };

    // Packet documents. Headers and bodies are stored but not indexed: nothing filters on them and
    // indexing multi-megabyte payloads would cost CPU and disk for no query benefit.
    private static JsonObject PacketLogMapping() => new()
    {
        ["dynamic"] = true,
        ["properties"] = new JsonObject
        {
            ["packet_id"] = new JsonObject { ["type"] = "keyword" },
            ["sequence"] = new JsonObject { ["type"] = "long" },
            ["kind"] = new JsonObject { ["type"] = "keyword" },
            ["page_number"] = new JsonObject { ["type"] = "integer" },
            ["attempt"] = new JsonObject { ["type"] = "integer" },
            ["step_execution_id"] = new JsonObject { ["type"] = "keyword" },
            ["flow_execution_id"] = new JsonObject { ["type"] = "keyword" },
            ["flow_id"] = new JsonObject { ["type"] = "keyword" },
            ["flow_name"] = new JsonObject { ["type"] = "keyword" },
            ["integration_id"] = new JsonObject { ["type"] = "keyword" },
            ["integration_name"] = new JsonObject { ["type"] = "keyword" },
            ["connection_id"] = new JsonObject { ["type"] = "keyword" },
            ["connection_name"] = new JsonObject { ["type"] = "keyword" },
            ["node_name"] = new JsonObject { ["type"] = "keyword" },
            ["is_manual"] = new JsonObject { ["type"] = "boolean" },
            ["http_method"] = new JsonObject { ["type"] = "keyword" },
            ["request_url"] = new JsonObject { ["type"] = "keyword" },
            ["status_code"] = new JsonObject { ["type"] = "integer" },
            ["duration_ms"] = new JsonObject { ["type"] = "long" },
            ["started_at"] = new JsonObject { ["type"] = "date" },
            ["completed_at"] = new JsonObject { ["type"] = "date" },
            ["error"] = new JsonObject { ["type"] = "text" },
            ["request_headers_json"] = new JsonObject { ["type"] = "text", ["index"] = false },
            ["response_headers_json"] = new JsonObject { ["type"] = "text", ["index"] = false },
            ["request_body"] = new JsonObject { ["type"] = "text", ["index"] = false },
            ["response_body"] = new JsonObject { ["type"] = "text", ["index"] = false },
            ["request_headers_size"] = new JsonObject { ["type"] = "integer" },
            ["request_body_size"] = new JsonObject { ["type"] = "integer" },
            ["response_headers_size"] = new JsonObject { ["type"] = "integer" },
            ["response_body_size"] = new JsonObject { ["type"] = "integer" }
        }
    };

    // Cross-reference entries. Key and value are both searchable (the list page searches across
    // them) and the document id is derived from list + key, which is what makes
    // "insert only if the key is new" work — create on an existing id always conflicts.
    private static JsonObject CrossReferenceMapping() => new()
    {
        ["dynamic"] = true,
        ["properties"] = new JsonObject
        {
            ["entry_id"] = new JsonObject { ["type"] = "keyword" },
            ["list_name"] = new JsonObject { ["type"] = "keyword" },
            ["key_value"] = new JsonObject
            {
                ["type"] = "text",
                ["fields"] = new JsonObject { ["keyword"] = new JsonObject { ["type"] = "keyword", ["ignore_above"] = 512 } }
            },
            ["value_json"] = new JsonObject { ["type"] = "text" },
            ["created_at"] = new JsonObject { ["type"] = "date" },
            ["flow_id"] = new JsonObject { ["type"] = "keyword" },
            ["flow_name"] = new JsonObject { ["type"] = "keyword" },
            ["integration_id"] = new JsonObject { ["type"] = "keyword" },
            ["integration_name"] = new JsonObject { ["type"] = "keyword" }
        }
    };
}
