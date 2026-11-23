using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Models.Storage;

// A dataset the settings page can describe: what it holds, whether it can move to
// Elasticsearch, and the defaults that preserve today's behaviour when nothing is configured.
public sealed record StorageDatasetDefinition(
    string Dataset,
    string Name,
    string Holds,
    string VolumeHint,
    bool CanUseElasticsearch,
    bool CanUseIlm,
    StorageGranularity DefaultGranularity,
    int DefaultRetentionDays,
    bool DefaultUseIlm,
    string SqlStore);

public static class StorageCatalog
{
    // Defaults mirror the behaviour that existed before this page: logs keep 72 hours of
    // detail (and longer for warnings and errors, handled by the relational log writer),
    // while packets, cross references and executions were never pruned. Configuring a
    // retention of 0 therefore changes nothing — nothing is deleted behind the operator's back.
    public static IReadOnlyList<StorageDatasetDefinition> Definitions { get; } = new[]
    {
        new StorageDatasetDefinition(
            StorageDatasets.Logs,
            "Application logs",
            "Debug Logs console: every log line from the API and the Engine, with execution correlation.",
            "Highest volume; dozens of fields per line.",
            CanUseElasticsearch: true,
            CanUseIlm: true,
            StorageGranularity.Daily,
            DefaultRetentionDays: 3,
            DefaultUseIlm: true,
            "AppLogEntries"),

        new StorageDatasetDefinition(
            StorageDatasets.PacketLogs,
            "Messages and payloads",
            "Packet Logs: request and response headers and bodies for every card that ran.",
            "Large documents; bodies are stored verbatim.",
            CanUseElasticsearch: true,
            CanUseIlm: true,
            StorageGranularity.Daily,
            DefaultRetentionDays: 0,
            DefaultUseIlm: true,
            "StepPacketLogs"),

        new StorageDatasetDefinition(
            StorageDatasets.CrossReferences,
            "Cross references",
            "Cross-reference entries written by Cross-Reference Store cards and read by Cross-Reference Filter cards.",
            "Grows with distinct keys; lists themselves stay in the database.",
            CanUseElasticsearch: true,
            CanUseIlm: true,
            StorageGranularity.Monthly,
            DefaultRetentionDays: 0,
            DefaultUseIlm: true,
            "CrossReferenceEntries"),

        new StorageDatasetDefinition(
            StorageDatasets.Executions,
            "Runs, steps and dead letters",
            "Transaction history, step results and the dead-letter queue.",
            "The Engine claims queued runs and dead letters with atomic conditional updates, so this dataset must stay in the database.",
            CanUseElasticsearch: false,
            CanUseIlm: false,
            StorageGranularity.Monthly,
            DefaultRetentionDays: 0,
            DefaultUseIlm: false,
            "FlowExecutions")
    };

    public static StorageDatasetDefinition Definition(string dataset) =>
        Definitions.FirstOrDefault(definition => string.Equals(definition.Dataset, dataset, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentOutOfRangeException(nameof(dataset), dataset, "Unknown storage dataset.");

    public static bool IsKnown(string dataset) =>
        Definitions.Any(definition => string.Equals(definition.Dataset, dataset, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<StorageDatasetSettings> Defaults() =>
        Definitions.Select(definition => new StorageDatasetSettings
        {
            Dataset = definition.Dataset,
            UseElasticsearch = false,
            Granularity = definition.DefaultGranularity,
            RetentionDays = definition.DefaultRetentionDays,
            UseIlm = definition.DefaultUseIlm
        }).ToList();

    // Tolerant read: unknown datasets are dropped and out-of-range values are clamped, so a
    // hand-edited column or an older row can never take a host down.
    public static IReadOnlyList<StorageDatasetSettings> Parse(string? json)
    {
        var parsed = new List<StorageDatasetSettings>();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<StorageDatasetSettings>>(json, JsonOptions) ?? new List<StorageDatasetSettings>();
                parsed.AddRange(items.Where(item => item != null && IsKnown(item.Dataset)));
            }
            catch (JsonException)
            {
                // Fall through to defaults below.
            }
        }

        var byDataset = parsed
            .GroupBy(item => item.Dataset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Any dataset missing from the stored JSON takes its documented default, so a dataset
        // added in a later release behaves exactly as it did before the setting existed.
        return Definitions.Select(definition =>
        {
            var settings = byDataset.TryGetValue(definition.Dataset, out var stored)
                ? stored.Clone()
                : new StorageDatasetSettings
                {
                    Dataset = definition.Dataset,
                    Granularity = definition.DefaultGranularity,
                    RetentionDays = definition.DefaultRetentionDays,
                    UseIlm = definition.DefaultUseIlm
                };

            settings.Dataset = definition.Dataset;
            settings.UseElasticsearch = definition.CanUseElasticsearch && settings.UseElasticsearch;
            settings.UseIlm = definition.CanUseIlm && settings.UseIlm;
            settings.RetentionDays = settings.RetentionDays < 0 ? 0 : settings.RetentionDays > 3650 ? 3650 : settings.RetentionDays;
            return settings;
        }).ToList();
    }

    public static string Serialize(IEnumerable<StorageDatasetSettings> settings) =>
        JsonSerializer.Serialize(settings.Select(item => item.Clone()).ToList(), JsonOptions);

    public static StorageSettingsSnapshot Snapshot(StorageSettings? row)
    {
        if (row == null)
        {
            return new StorageSettingsSnapshot
            {
                Datasets = Defaults().ToDictionary(item => item.Dataset, item => item, StringComparer.OrdinalIgnoreCase)
            };
        }

        var datasets = Parse(row.DatasetsJson).ToDictionary(item => item.Dataset, item => item, StringComparer.OrdinalIgnoreCase);

        // Every persisted column that affects the connection is mapped here. The username matters
        // as much as the URL: dropping it keeps the settings page showing an empty field and the
        // client never sends the Authorization: Basic header (API keys are applied elsewhere).
        return new StorageSettingsSnapshot
        {
            ElasticsearchEnabled = row.ElasticsearchEnabled,
            IndexPrefix = SanitizePrefix(row.IndexPrefix),
            Url = (row.ElasticsearchUrl ?? string.Empty).Trim(),
            Username = (row.ElasticsearchUsername ?? string.Empty).Trim(),
            AllowInvalidCertificate = row.AllowInvalidCertificate,
            TimeoutSeconds = row.TimeoutSeconds < 1 ? 15 : Math.Min(row.TimeoutSeconds, 300),
            Datasets = datasets
        };
    }

    // Used for the index prefix and dataset ids inside index names: lowercase letters, digits,
    // dashes only. Elasticsearch rejects anything else in an index name.
    public static string SanitizePrefix(string? prefix)
    {
        var cleaned = new string((prefix ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');

        return string.IsNullOrEmpty(cleaned) ? "fusetor" : cleaned[..Math.Min(cleaned.Length, 40)];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
