using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Validates and persists the storage settings, then prepares the index plan for any dataset that
// was just switched to Elasticsearch — so "save" and "the dataset works" are the same action.
public sealed class StorageSettingsWriter
{
    private readonly IPaaSContext _context;
    private readonly IEncryptionService _encryption;
    private readonly IStorageSettingsProvider _settings;
    private readonly ElasticsearchIndexManager _indexManager;
    private readonly StorageRuntime _runtime;

    public StorageSettingsWriter(
        IPaaSContext context,
        IEncryptionService encryption,
        IStorageSettingsProvider settings,
        ElasticsearchIndexManager indexManager,
        StorageRuntime runtime)
    {
        _context = context;
        _encryption = encryption;
        _settings = settings;
        _indexManager = indexManager;
        _runtime = runtime;
    }

    // ValidationFailed distinguishes "nothing was saved because the input is invalid" (the API
    // answers 400) from a save that succeeded but could not prepare an Elasticsearch index plan.
    public sealed record SaveOutcome(
        bool Success,
        string Message,
        StorageSettingsSnapshot Snapshot,
        System.Collections.Generic.IReadOnlyList<StorageApplyOutcome> Applied,
        bool ValidationFailed = false);

    public async Task<SaveOutcome> SaveAsync(StorageSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        var validation = Validate(update);
        if (validation != null)
        {
            return new SaveOutcome(false, validation, await _settings.GetAsync(cancellationToken).ConfigureAwait(false), Array.Empty<StorageApplyOutcome>(), ValidationFailed: true);
        }

        var row = await _context.StorageSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var isNew = row == null;
        row ??= new StorageSettings { Id = 1 };

        row.ElasticsearchEnabled = update.ElasticsearchEnabled;
        row.ElasticsearchUrl = (update.Url ?? string.Empty).Trim();
        row.ElasticsearchUsername = (update.Username ?? string.Empty).Trim();
        row.IndexPrefix = StorageCatalog.SanitizePrefix(update.IndexPrefix);
        row.AllowInvalidCertificate = update.AllowInvalidCertificate;
        row.TimeoutSeconds = update.TimeoutSeconds < 1 ? 15 : Math.Min(update.TimeoutSeconds, 300);
        var datasets = Normalize(update.Datasets);
        row.DatasetsJson = StorageCatalog.Serialize(datasets);
        row.UpdatedAt = DateTime.UtcNow;

        if (update.ClearPassword)
        {
            row.EncryptedElasticsearchPassword = string.Empty;
        }
        else if (!string.IsNullOrEmpty(update.Password))
        {
            row.EncryptedElasticsearchPassword = await _encryption.EncryptAsync(update.Password).ConfigureAwait(false);
        }

        if (update.ClearApiKey)
        {
            row.EncryptedElasticsearchApiKey = string.Empty;
        }
        else if (!string.IsNullOrEmpty(update.ApiKey))
        {
            row.EncryptedElasticsearchApiKey = await _encryption.EncryptAsync(update.ApiKey).ConfigureAwait(false);
        }

        if (isNew)
        {
            _context.StorageSettings.Add(row);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The host that saved switches immediately; the other host follows within the cache window.
        var snapshot = await ReloadAsync(row, cancellationToken).ConfigureAwait(false);
        _settings.Invalidate();

        // The routing state every hot path reads is updated right away rather than on the next tick.
        _runtime.Invalidate();

        var applied = new System.Collections.Generic.List<StorageApplyOutcome>();
        if (snapshot.ElasticsearchEnabled && snapshot.IsElasticsearchConfigured)
        {
            foreach (var dataset in StorageIndexNaming.DatasetsUsingElasticsearch(snapshot))
            {
                applied.Add(await _indexManager.EnsureAsync(dataset, snapshot, cancellationToken).ConfigureAwait(false));
            }
        }

        var indexFailures = applied.Count(outcome => !outcome.Success);
        var message = new System.Text.StringBuilder(indexFailures == 0
            ? "Storage settings saved."
            : $"Storage settings saved, but {indexFailures} dataset(s) could not be prepared in Elasticsearch — see the status panel.");

        // Clamps are legal but must not be silent: an operator who typed 99999 days has to be told
        // the value that was actually stored.
        foreach (var clamp in ClampNotes(update.Datasets))
        {
            message.Append(' ').Append(clamp);
        }

        return new SaveOutcome(indexFailures == 0, message.ToString(), snapshot, applied);
    }

    private async Task<StorageSettingsSnapshot> ReloadAsync(StorageSettings row, CancellationToken cancellationToken)
    {
        var reloaded = await _context.StorageSettings.AsNoTracking().FirstAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = StorageCatalog.Snapshot(reloaded);

        var password = string.IsNullOrEmpty(reloaded.EncryptedElasticsearchPassword)
            ? string.Empty
            : await _encryption.DecryptAsync(reloaded.EncryptedElasticsearchPassword).ConfigureAwait(false);
        var apiKey = string.IsNullOrEmpty(reloaded.EncryptedElasticsearchApiKey)
            ? string.Empty
            : await _encryption.DecryptAsync(reloaded.EncryptedElasticsearchApiKey).ConfigureAwait(false);

        return snapshot with { Password = password, ApiKey = apiKey };
    }

    // The values Normalize would silently change, phrased for the save response so the operator
    // knows what was actually stored.
    private static System.Collections.Generic.List<string> ClampNotes(
        System.Collections.Generic.IReadOnlyList<StorageDatasetSettings> submitted)
    {
        var notes = new System.Collections.Generic.List<string>();

        foreach (var dataset in submitted
            .Where(item => item != null && StorageCatalog.IsKnown(item.Dataset))
            .GroupBy(item => item.Dataset, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var definition = StorageCatalog.Definition(dataset.Dataset);

            if (dataset.RetentionDays > 3650)
            {
                notes.Add($"Retention for {definition.Name} was capped at 3,650 days (10 years).");
            }

            if (dataset.UseIlm && !definition.CanUseIlm)
            {
                notes.Add($"ILM was switched off for {definition.Name}: it stays in the database.");
            }
        }

        return notes;
    }

    private static System.Collections.Generic.List<StorageDatasetSettings> Normalize(
        System.Collections.Generic.IReadOnlyList<StorageDatasetSettings> datasets)
    {
        // Datasets that are not in the submitted list keep their stored/default configuration, so a
        // client that only knows about a subset of datasets cannot wipe the rest.
        var submitted = datasets
            .Where(item => item != null && StorageCatalog.IsKnown(item.Dataset))
            .GroupBy(item => item.Dataset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return StorageCatalog.Defaults()
            .Select(defaults =>
            {
                if (!submitted.TryGetValue(defaults.Dataset, out var incoming))
                {
                    return defaults;
                }

                var definition = StorageCatalog.Definition(defaults.Dataset);
                return new StorageDatasetSettings
                {
                    Dataset = defaults.Dataset,
                    UseElasticsearch = definition.CanUseElasticsearch && incoming.UseElasticsearch,
                    Granularity = incoming.Granularity,
                    RetentionDays = incoming.RetentionDays < 0 ? 0 : Math.Min(incoming.RetentionDays, 3650),
                    UseIlm = definition.CanUseIlm && incoming.UseIlm
                };
            })
            .ToList();
    }

    private static string? Validate(StorageSettingsUpdate update)
    {
        if (update.ElasticsearchEnabled || update.Datasets.Any(item => item != null && item.UseElasticsearch))
        {
            if (string.IsNullOrWhiteSpace(update.Url))
            {
                return "Enter the Elasticsearch URL before switching a dataset over to it.";
            }

            if (!Uri.TryCreate(update.Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "The Elasticsearch URL must be an absolute http:// or https:// address, for example http://localhost:9200.";
            }
        }

        foreach (var dataset in update.Datasets.Where(item => item != null))
        {
            if (!StorageCatalog.IsKnown(dataset.Dataset))
            {
                return $"'{dataset.Dataset}' is not a storage dataset this build knows about.";
            }

            var definition = StorageCatalog.Definition(dataset.Dataset);
            if (dataset.UseElasticsearch && !definition.CanUseElasticsearch)
            {
                return $"{definition.Name} cannot be moved to Elasticsearch: {definition.VolumeHint}";
            }

            if (dataset.RetentionDays < 0)
            {
                return "Retention is a number of days; use 0 to keep data forever.";
            }
        }

        return null;
    }
}
