using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Everything the storage settings page acts through: verify the cluster, create or repair the
// index plan, report what exists, and run retention on demand.
public sealed class StorageOperations : IStorageOperations
{
    private readonly ElasticsearchClientProvider _clients;
    private readonly ElasticsearchIndexManager _indexManager;
    private readonly IStorageSettingsProvider _settings;
    private readonly StorageRetentionService _retention;
    private readonly IServiceScopeFactory _scopeFactory;

    public StorageOperations(
        ElasticsearchClientProvider clients,
        ElasticsearchIndexManager indexManager,
        IStorageSettingsProvider settings,
        StorageRetentionService retention,
        IServiceScopeFactory scopeFactory)
    {
        _clients = clients;
        _indexManager = indexManager;
        _settings = settings;
        _retention = retention;
        _scopeFactory = scopeFactory;
    }

    public async Task<StorageConnectionTestResult> TestConnectionAsync(
        StorageConnectionTestRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var stored = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var submitted = request != null && !string.IsNullOrWhiteSpace(request.Url);

        // The submitted values win when the page sends them, and nothing is persisted. A blank
        // secret keeps the stored one — the same rule the save endpoint uses.
        var effective = submitted
            ? stored with
            {
                // The operator is testing the URL they are about to enable, so the master switch
                // must not veto the call.
                ElasticsearchEnabled = true,
                Url = request!.Url.Trim(),
                Username = (request.Username ?? string.Empty).Trim(),
                Password = string.IsNullOrEmpty(request.Password) ? stored.Password : request.Password!,
                ApiKey = string.IsNullOrEmpty(request.ApiKey) ? stored.ApiKey : request.ApiKey!,
                AllowInvalidCertificate = request.AllowInvalidCertificate,
                TimeoutSeconds = request.TimeoutSeconds < 1 ? 15 : Math.Min(request.TimeoutSeconds, 300)
            }
            : stored;

        if (!effective.IsElasticsearchConfigured)
        {
            return new StorageConnectionTestResult(false, "No cluster URL is configured yet.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            ElasticsearchClusterInfo info;
            if (submitted)
            {
                // A dedicated client for the submitted values: the cached client is untouched and
                // the configuration is not persisted.
                using var client = _clients.CreateClient(effective);
                info = await client.PingAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var resolved = await _clients.GetAsync(cancellationToken).ConfigureAwait(false);
                if (resolved == null)
                {
                    return new StorageConnectionTestResult(false, "Elasticsearch is switched off for this host.");
                }

                info = await resolved.Value.Client.PingAsync(cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return Describe(info, stopwatch);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new StorageConnectionTestResult(false, exception.Message, DurationMs: (int)stopwatch.ElapsedMilliseconds);
        }
    }

    private static StorageConnectionTestResult Describe(ElasticsearchClusterInfo info, Stopwatch stopwatch)
    {
        var message = $"Connected to {info.ClusterName} (node {info.NodeName}, Elasticsearch {info.Version}).";
        if (!string.Equals(info.Product, "Elasticsearch", StringComparison.OrdinalIgnoreCase))
        {
            message += " The cluster did not identify itself as Elasticsearch; index templates and ILM may not be supported.";
        }

        return new StorageConnectionTestResult(true, message, info.ClusterName, info.Version, info.Product, (int)stopwatch.ElapsedMilliseconds);
    }

    public async Task<StorageApplyResult> ApplyIndexPlanAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var datasets = StorageIndexNaming.DatasetsUsingElasticsearch(snapshot);

        if (datasets.Count == 0)
        {
            return new StorageApplyResult(false, "No dataset is configured for Elasticsearch, so there is nothing to create.", Array.Empty<StorageApplyOutcome>());
        }

        var outcomes = new List<StorageApplyOutcome>(datasets.Count);
        foreach (var dataset in datasets)
        {
            outcomes.Add(await _indexManager.EnsureAsync(dataset, snapshot, cancellationToken).ConfigureAwait(false));
        }

        var failures = outcomes.Count(outcome => !outcome.Success);
        var message = failures == 0
            ? $"Index templates, ILM policies and write aliases are in place for {outcomes.Count} dataset(s)."
            : $"{failures} of {outcomes.Count} dataset(s) could not be prepared; see the details below.";

        return new StorageApplyResult(failures == 0, message, outcomes);
    }

    public async Task<StorageInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var statuses = new List<StorageDatasetStatus>();
        string? error = null;

        foreach (var dataset in StorageDatasets.All)
        {
            var definition = StorageCatalog.Definition(dataset);
            var datasetSettings = snapshot.For(dataset);
            var prefix = snapshot.IndexPrefix;
            var writeAlias = StorageIndexNaming.WriteAlias(prefix, dataset);
            var pattern = StorageIndexNaming.Pattern(prefix, dataset);
            var templateName = StorageIndexNaming.TemplateName(prefix, dataset);
            var policyName = StorageIndexNaming.PolicyName(prefix, dataset);

            var backend = definition.CanUseElasticsearch && snapshot.UsesElasticsearch(dataset) ? "Elasticsearch" : "Database";
            var documentCount = 0L;
            var indices = new List<StorageIndexInfo>();
            var ready = backend == "Database";
            string? datasetError = null;

            if (backend == "Elasticsearch")
            {
                try
                {
                    var resolved = await _clients.GetAsync(cancellationToken).ConfigureAwait(false);
                    if (resolved == null)
                    {
                        datasetError = "Elasticsearch is switched off for this host.";
                    }
                    else
                    {
                        var now = DateTime.UtcNow;
                        var listed = await _indexManager.ListDatasetIndicesAsync(snapshot, dataset, cancellationToken).ConfigureAwait(false);
                        foreach (var index in listed)
                        {
                            var period = StorageIndexNaming.PeriodOf(prefix, dataset, index.Name);
                            indices.Add(new StorageIndexInfo(
                                index.Name,
                                index.DocumentCount,
                                index.Size,
                                index.CreatedAtUtc,
                                StorageIndexNaming.AgeInDays(index.CreatedAtUtc, now),
                                period != null && StorageIndexNaming.IsExpired(period.Value, datasetSettings.RetentionDays, now)));
                        }

                        documentCount = indices.Sum(index => index.DocumentCount);
                        ready = indices.Any(index => index.Name == StorageIndexNaming.CurrentIndex(snapshot, dataset))
                            || indices.Count > 0;
                    }
                }
                catch (Exception exception) when (exception is ElasticsearchRequestException or InvalidOperationException)
                {
                    datasetError = exception.Message;
                    error ??= exception.Message;
                }
            }
            else
            {
                documentCount = await CountRelationalAsync(dataset, cancellationToken).ConfigureAwait(false);
            }

            statuses.Add(new StorageDatasetStatus(
                dataset,
                definition.Name,
                backend,
                definition.CanUseElasticsearch,
                datasetSettings.UseIlm,
                datasetSettings.RetentionDays,
                definition.SqlStore,
                documentCount,
                writeAlias,
                pattern,
                templateName,
                policyName,
                ready,
                datasetError,
                indices));
        }

        return new StorageInventory(DateTime.UtcNow, statuses, error);
    }

    public Task<StorageRetentionRunResult> RunRetentionAsync(CancellationToken cancellationToken = default) =>
        _retention.RunAsync(cancellationToken);

    private async Task<long> CountRelationalAsync(string dataset, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IPaaSContext>();

        try
        {
            return dataset switch
            {
                StorageDatasets.Logs => await context.AppLogEntries.IgnoreQueryFilters().LongCountAsync(cancellationToken),
                StorageDatasets.PacketLogs => await context.StepPacketLogs.IgnoreQueryFilters().LongCountAsync(cancellationToken),
                StorageDatasets.CrossReferences => await context.CrossReferenceEntries.IgnoreQueryFilters().LongCountAsync(cancellationToken),
                StorageDatasets.Executions => await context.FlowExecutions.IgnoreQueryFilters().LongCountAsync(cancellationToken),
                _ => 0L
            };
        }
        catch (Exception)
        {
            // A status page must render even if one count fails.
            return 0L;
        }
    }
}
