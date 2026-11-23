using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Retention across both backends.
//
// Elasticsearch: expired *periods* are removed — either by the ILM policy's delete phase (created
// and kept in sync here) or, when ILM is switched off for a dataset, by deleting the backing
// indices that fell out of the window. Nothing is ever partially deleted from a live index.
//
// Relational: rows older than the window are deleted in bounded batches, from the
// datasets that stay in the database. The application log table keeps its own pruning in the log
// sink (same configured window), because that table is written on a non-blocking channel that must
// not contend with a maintenance sweep.
public sealed class StorageRetentionService
{
    private const int BatchSize = 5000;
    private const int MaxBatchesPerPass = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStorageSettingsProvider _settings;
    private readonly ElasticsearchClientProvider _clients;
    private readonly ElasticsearchIndexManager _indexManager;
    private readonly ILogger<StorageRetentionService> _logger;

    public StorageRetentionService(
        IServiceScopeFactory scopeFactory,
        IStorageSettingsProvider settings,
        ElasticsearchClientProvider clients,
        ElasticsearchIndexManager indexManager,
        ILogger<StorageRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _clients = clients;
        _indexManager = indexManager;
        _logger = logger;
    }

    public async Task<StorageRetentionRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = new List<StorageRetentionOutcome>();

        foreach (var dataset in StorageDatasets.All)
        {
            var datasetSettings = snapshot.For(dataset);
            var definition = StorageCatalog.Definition(dataset);

            if (definition.CanUseElasticsearch && snapshot.UsesElasticsearch(dataset))
            {
                outcomes.Add(await RunElasticsearchAsync(dataset, datasetSettings, snapshot, cancellationToken).ConfigureAwait(false));
                continue;
            }

            outcomes.Add(await RunRelationalAsync(dataset, datasetSettings, cancellationToken).ConfigureAwait(false));
        }

        return new StorageRetentionRunResult(DateTime.UtcNow, outcomes);
    }

    private async Task<StorageRetentionOutcome> RunElasticsearchAsync(
        string dataset,
        StorageDatasetSettings settings,
        StorageSettingsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var prefix = snapshot.IndexPrefix;

        if (settings.RetentionDays <= 0)
        {
            return new StorageRetentionOutcome(
                dataset,
                "Elasticsearch",
                $"Keeping every index ({StorageIndexNaming.Pattern(prefix, dataset)}); no retention configured",
                0);
        }

        try
        {
            if (settings.UseIlm)
            {
                var policyName = StorageIndexNaming.PolicyName(prefix, dataset);
                var resolved = await _clients.GetAsync(cancellationToken).ConfigureAwait(false);
                if (resolved == null)
                {
                    return new StorageRetentionOutcome(dataset, "Elasticsearch", "Cluster unavailable", 0, "No cluster is configured or reachable.");
                }

                await resolved.Value.Client.PutIlmPolicyAsync(
                    policyName,
                    ElasticsearchIndexTemplates.BuildIlmPolicy(dataset, settings.Granularity, settings.RetentionDays),
                    cancellationToken).ConfigureAwait(false);

                return new StorageRetentionOutcome(
                    dataset,
                    "Elasticsearch",
                    $"Handled by ILM policy {policyName}: delete phase at {settings.RetentionDays}d",
                    0);
            }

            var (deleted, documents, names) = await _indexManager.DeleteExpiredAsync(dataset, snapshot, cancellationToken).ConfigureAwait(false);
            var action = deleted == 0
                ? "No expired indices"
                : $"Deleted {deleted} expired index/indices ({string.Join(", ", names)})";
            return new StorageRetentionOutcome(dataset, "Elasticsearch", action, documents);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Elasticsearch retention failed for dataset {Dataset}.", dataset);
            return new StorageRetentionOutcome(dataset, "Elasticsearch", "Failed", 0, exception.Message);
        }
    }

    private async Task<StorageRetentionOutcome> RunRelationalAsync(
        string dataset,
        StorageDatasetSettings settings,
        CancellationToken cancellationToken)
    {
        if (dataset == StorageDatasets.Logs)
        {
            return new StorageRetentionOutcome(
                dataset,
                "Database",
                settings.RetentionDays <= 0
                    ? "Keeping every log entry (row cap still applies)"
                    : $"Applied by the log sink every 5 minutes ({settings.RetentionDays}d)",
                0);
        }

        if (settings.RetentionDays <= 0)
        {
            return new StorageRetentionOutcome(dataset, "Database", "Keeping every row (no retention configured)", 0);
        }

        var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IPaaSContext>();

            var removed = dataset switch
            {
                StorageDatasets.PacketLogs => await DeleteInBatchesAsync(
                    () => context.StepPacketLogs.IgnoreQueryFilters().Where(packet => packet.StartedAt < cutoff).Select(packet => packet.Id).Take(BatchSize).ToListAsync(cancellationToken),
                    ids => context.StepPacketLogs.IgnoreQueryFilters().Where(packet => ids.Contains(packet.Id)).ExecuteDeleteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false),

                StorageDatasets.CrossReferences => await DeleteInBatchesAsync(
                    () => context.CrossReferenceEntries.IgnoreQueryFilters().Where(entry => entry.CreatedAt < cutoff).Select(entry => entry.Id).Take(BatchSize).ToListAsync(cancellationToken),
                    ids => context.CrossReferenceEntries.IgnoreQueryFilters().Where(entry => ids.Contains(entry.Id)).ExecuteDeleteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false),

                // Finished runs only: a queued or running execution must never be swept away, and
                // deleting the run cascades to its steps and (relational) packet logs.
                StorageDatasets.Executions => await DeleteInBatchesAsync(
                    () => context.FlowExecutions.IgnoreQueryFilters()
                        .Where(execution => !execution.IsTest
                            && execution.CompletedAt != null
                            && execution.CompletedAt < cutoff)
                        .Select(execution => execution.Id)
                        .Take(BatchSize)
                        .ToListAsync(cancellationToken),
                    ids => context.FlowExecutions.IgnoreQueryFilters().Where(execution => ids.Contains(execution.Id)).ExecuteDeleteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false),

                _ => 0
            };

            return new StorageRetentionOutcome(
                dataset,
                "Database",
                removed == 0 ? $"Nothing older than {settings.RetentionDays}d" : $"Deleted rows older than {settings.RetentionDays}d",
                removed);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Relational retention failed for dataset {Dataset}.", dataset);
            return new StorageRetentionOutcome(dataset, "Database", "Failed", 0, exception.Message);
        }
    }

    private static async Task<int> DeleteInBatchesAsync(
        Func<Task<List<Guid>>> selectBatch,
        Func<List<Guid>, Task<int>> deleteBatch,
        CancellationToken cancellationToken)
    {
        var removed = 0;

        for (var pass = 0; pass < MaxBatchesPerPass; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = await selectBatch().ConfigureAwait(false);
            if (ids.Count == 0)
            {
                break;
            }

            removed += await deleteBatch(ids).ConfigureAwait(false);

            if (ids.Count < BatchSize)
            {
                break;
            }
        }

        return removed;
    }
}
