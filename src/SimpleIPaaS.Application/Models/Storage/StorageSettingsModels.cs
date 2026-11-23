using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SimpleIPaaS.Application.Models.Storage;

// How often a new backing index is started for a dataset. This is what makes retention
// enforceable: one index per period means an expired period is removed by deleting a single
// index (or by letting an ILM delete phase do it) instead of running bulk deletes through the
// whole dataset.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageGranularity
{
    Daily,
    Weekly,
    Monthly
}

// The datasets that can be moved between the relational database and Elasticsearch, plus the
// relational-only datasets that still need a retention policy.
public static class StorageDatasets
{
    public const string Logs = "logs";
    public const string PacketLogs = "packet-logs";
    public const string CrossReferences = "cross-references";
    public const string Executions = "executions";

    // Every dataset the settings page renders, in display order.
    public static IReadOnlyList<string> All { get; } = new[] { Logs, PacketLogs, CrossReferences, Executions };
}

public sealed class StorageDatasetSettings
{
    public string Dataset { get; set; } = string.Empty;

    public bool UseElasticsearch { get; set; }

    public StorageGranularity Granularity { get; set; } = StorageGranularity.Daily;

    // Days of history to keep. 0 means keep forever.
    public int RetentionDays { get; set; }

    // Elasticsearch only: let an ILM policy delete expired indices instead of the app doing it.
    public bool UseIlm { get; set; } = true;

    public StorageDatasetSettings Clone() => new()
    {
        Dataset = Dataset,
        UseElasticsearch = UseElasticsearch,
        Granularity = Granularity,
        RetentionDays = RetentionDays,
        UseIlm = UseIlm
    };
}

// Everything the storage layer needs to route a dataset, with defaults already applied and
// secrets decrypted. Produced by StorageSettingsProvider and cached briefly in both hosts.
public sealed record StorageSettingsSnapshot
{
    public bool ElasticsearchEnabled { get; init; }

    public string IndexPrefix { get; init; } = "fusetor";

    public string Url { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public bool AllowInvalidCertificate { get; init; }

    public int TimeoutSeconds { get; init; } = 15;

    public IReadOnlyDictionary<string, StorageDatasetSettings> Datasets { get; init; } =
        new Dictionary<string, StorageDatasetSettings>(StringComparer.OrdinalIgnoreCase);

    public DateTime LoadedAtUtc { get; init; } = DateTime.UtcNow;

    public StorageDatasetSettings For(string dataset) =>
        Datasets.TryGetValue(dataset, out var settings)
            ? settings
            : new StorageDatasetSettings { Dataset = dataset };

    // The single question the stores ask: is this dataset served by Elasticsearch right now?
    public bool UsesElasticsearch(string dataset) => ElasticsearchEnabled && For(dataset).UseElasticsearch;

    public bool IsElasticsearchConfigured => !string.IsNullOrWhiteSpace(Url);

    // Safe for logs and API responses: never includes a credential.
    public string DescribeBackend(string dataset) =>
        UsesElasticsearch(dataset) ? $"Elasticsearch ({Url})" : "Database";

    public static StorageSettingsSnapshot Default { get; } = new();
}
