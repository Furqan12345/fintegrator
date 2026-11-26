using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
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
        await EnsurePostgresPartitionsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var dataset in StorageDatasets.All)
        {
            var datasetSettings = snapshot.For(dataset);
            var definition = StorageCatalog.Definition(dataset);

            if (definition.CanUseElasticsearch && snapshot.UsesElasticsearch(dataset))
            {
                outcomes.Add(await RunElasticsearchAsync(dataset, datasetSettings, snapshot, cancellationToken).ConfigureAwait(false));
                continue;
            }

            outcomes.Add(await RunRelationalAsync(dataset, datasetSettings, snapshot.For(StorageDatasets.Executions).RetentionDays, cancellationToken).ConfigureAwait(false));
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
        int runRetentionDays,
        CancellationToken cancellationToken)
    {
        if (dataset == StorageDatasets.Logs)
        {
            var warnings = StorageCatalog.LogWarningRetentionDays(settings);
            return new StorageRetentionOutcome(
                dataset,
                "Database",
                settings.RetentionDays <= 0 && warnings <= 0
                    ? "Keeping every log entry"
                    : $"Applied by the log sink every 5 minutes ({Days(settings.RetentionDays)}, warnings and errors {Days(warnings)})",
                0);
        }

        var keepForever = new StorageRetentionOutcome(dataset, "Database", "Keeping every row (no retention configured)", 0);
        if (settings.RetentionDays <= 0 && (dataset != StorageDatasets.PacketLogs || runRetentionDays <= 0))
        {
            return keepForever;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
            if (context.IsPostgres())
            {
                return await RunPostgresAsync(dataset, settings, runRetentionDays, context, scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>(), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (settings.RetentionDays <= 0)
            {
                return keepForever;
            }

            var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays);

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
                    () => ExpiredRuns(context, cutoff)
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

    // PostgreSQL: the append-only tables are partitioned by day, so expired history is dropped a day at a time
    // (detach concurrently + drop) instead of deleted row by row. Runs and steps are not partitioned (foreign keys
    // point at them); with their payloads in the cold tables they are narrow, so the batched delete stays cheap.
    //
    // Deleting a run does not delete its packet logs or step payloads row by row: their days are dropped instead.
    //  * Packet logs are dropped at the shorter of the two windows, so they never outlive their runs by more than
    //    the one-day granularity (a packet whose run is gone cannot be opened from anywhere).
    //  * Step payloads follow the runs window.
    // Rows of a run deleted today stay until their day is dropped (at most about a day), unreachable from the UI.
    // Deleting them with the run instead turned every expired run into row-by-row deletes in the partitioned tables
    // (runs cross the cutoff continuously, their day becomes droppable up to a day later), which is what the
    // partitions exist to avoid. Explicit deletes (a flow, test runs, the demo reseed) still remove them at once.
    private static string Days(int days) => days <= 0 ? "forever" : $"{days}d";

    internal static int EffectivePacketRetentionDays(int packetRetentionDays, int runRetentionDays) =>
        packetRetentionDays > 0 && runRetentionDays > 0
            ? Math.Min(packetRetentionDays, runRetentionDays)
            : Math.Max(packetRetentionDays, runRetentionDays);

    private async Task<StorageRetentionOutcome> RunPostgresAsync(
        string dataset,
        StorageDatasetSettings settings,
        int runRetentionDays,
        IPaaSContext context,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        long removed = 0;
        var partitions = 0;
        var days = settings.RetentionDays;
        switch (dataset)
        {
            case StorageDatasets.PacketLogs:
            {
                days = EffectivePacketRetentionDays(settings.RetentionDays, runRetentionDays);
                var cutoff = DateTime.UtcNow.AddDays(-days);
                var hot = await PostgresPartitions.DropOlderThanAsync(dataSource, PostgresLayout.StepPacketLogs, cutoff, cancellationToken).ConfigureAwait(false);
                var cold = await PostgresPartitions.DropOlderThanAsync(dataSource, PostgresLayout.StepPacketPayloads, cutoff, cancellationToken).ConfigureAwait(false);
                removed = hot.Rows;
                partitions = hot.Partitions + cold.Partitions;
                break;
            }

            case StorageDatasets.Executions:
            {
                var cutoff = DateTime.UtcNow.AddDays(-days);
                var cold = await PostgresPartitions.DropOlderThanAsync(dataSource, PostgresLayout.StepExecutionPayloads, cutoff, cancellationToken).ConfigureAwait(false);
                partitions = cold.Partitions;
                // Steps, dead letters, Fan Out snapshots and the run and dead-letter payload rows cascade.
                removed = await DeleteInBatchesAsync(
                    () => ExpiredRuns(context, cutoff)
                        .Select(execution => execution.Id)
                        .Take(BatchSize)
                        .ToListAsync(cancellationToken),
                    ids => context.FlowExecutions.IgnoreQueryFilters().Where(execution => ids.Contains(execution.Id)).ExecuteDeleteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            case StorageDatasets.CrossReferences:
            {
                var cutoff = DateTime.UtcNow.AddDays(-days);
                removed = await DeleteInBatchesAsync(
                    () => context.CrossReferenceEntries.IgnoreQueryFilters().Where(entry => entry.CreatedAt < cutoff).Select(entry => entry.Id).Take(BatchSize).ToListAsync(cancellationToken),
                    ids => context.CrossReferenceEntries.IgnoreQueryFilters().Where(entry => ids.Contains(entry.Id)).ExecuteDeleteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        var window = days != settings.RetentionDays ? $"{days}d, the runs window" : $"{days}d";
        var status = removed == 0 && partitions == 0
            ? $"Nothing older than {window}"
            : partitions > 0
                ? $"Dropped {partitions} day partition(s) older than {window}"
                : $"Deleted rows older than {window}";
        return new StorageRetentionOutcome(dataset, "Database", status, (int)Math.Min(removed, int.MaxValue));
    }

    // Every pass also keeps a week of future day partitions in place (cheap when they already exist).
    private async Task EnsurePostgresPartitionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dataSource = scope.ServiceProvider.GetService<NpgsqlDataSource>();
            if (dataSource == null)
            {
                return;
            }

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var skipped = await PostgresPartitions.EnsureAheadAsync(connection, cancellationToken).ConfigureAwait(false);
            if (skipped.Count > 0)
            {
                _logger.LogInformation(
                    "Upcoming PostgreSQL day partitions for {Tables} were not created: the table was busy for {Seconds}s. The next pass retries (a week is kept ahead).",
                    string.Join(", ", skipped), PostgresPartitions.MaintenanceLockTimeout.TotalSeconds);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Creating the upcoming PostgreSQL day partitions failed; writers create a missing day on demand.");
        }
    }

    // Finished, non-test runs past the cutoff. A Fan Out parent stays while any of its child runs still uses its
    // snapshot (the children start from it and their dead letters rebuild their flow state from it); it goes on a
    // later pass, after the children.
    private static IQueryable<FlowExecution> ExpiredRuns(IPaaSContext context, DateTime cutoff) =>
        context.FlowExecutions.IgnoreQueryFilters()
            .Where(execution => !execution.IsTest
                && execution.CompletedAt != null
                && execution.CompletedAt < cutoff
                && !context.FanOutSnapshots.Any(snapshot => snapshot.ParentExecutionId == execution.Id
                    && context.FlowExecutions.Any(child => child.FanOutSnapshotId == snapshot.Id)));

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
