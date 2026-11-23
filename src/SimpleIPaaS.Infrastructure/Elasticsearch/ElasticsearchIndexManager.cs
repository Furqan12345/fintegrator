using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Owns the index plan for every dataset that uses Elasticsearch:
//
//   * the index template (mappings + settings + ILM policy attachment) per dataset;
//   * the ILM policy when the operator wants Elasticsearch to expire periods itself;
//   * the current period's backing index, created with the write alias pointing at it.
//
// Writers never compute an index name: they write to <prefix>-<dataset>-write and this type keeps
// that alias on the current period. That is also what lets ILM roll an index over without the
// application noticing.
//
// The client is resolved per call from the settings snapshot rather than injected, so a change to
// the cluster URL, index prefix or credentials takes effect without restarting either host.
public sealed class ElasticsearchIndexManager
{
    private readonly ElasticsearchClientProvider _clients;

    public ElasticsearchIndexManager(ElasticsearchClientProvider clients)
    {
        _clients = clients;
    }

    public async Task<StorageApplyOutcome> EnsureAsync(string dataset, StorageSettingsSnapshot settings, CancellationToken cancellationToken = default)
    {
        var definition = StorageCatalog.Definition(dataset);
        var datasetSettings = settings.For(dataset);
        var prefix = settings.IndexPrefix;
        var writeAlias = StorageIndexNaming.WriteAlias(prefix, dataset);
        var templateName = StorageIndexNaming.TemplateName(prefix, dataset);
        var policyName = StorageIndexNaming.PolicyName(prefix, dataset);
        var currentIndex = StorageIndexNaming.BackingIndex(prefix, dataset, datasetSettings.Granularity, DateTime.UtcNow);

        try
        {
            var client = await RequireClientAsync(cancellationToken).ConfigureAwait(false);

            if (datasetSettings.UseIlm && definition.CanUseIlm)
            {
                await client.PutIlmPolicyAsync(
                    policyName,
                    ElasticsearchIndexTemplates.BuildIlmPolicy(dataset, datasetSettings.Granularity, datasetSettings.RetentionDays),
                    cancellationToken).ConfigureAwait(false);
            }

            await client.PutIndexTemplateAsync(
                templateName,
                ElasticsearchIndexTemplates.BuildIndexTemplate(settings, dataset),
                cancellationToken).ConfigureAwait(false);

            if (!await client.IndexExistsAsync(currentIndex, cancellationToken).ConfigureAwait(false))
            {
                // An index that the template already covers needs nothing but its write alias.
                await client.CreateIndexAsync(
                    currentIndex,
                    ElasticsearchIndexTemplates.BuildCreateIndexBody(writeAlias),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Put it back on the alias in case a rollover or a manual deletion moved it away.
                await client.UpdateAliasesAsync(
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["add"] = new JsonObject
                            {
                                ["index"] = currentIndex,
                                ["alias"] = writeAlias,
                                ["is_write_index"] = true
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return new StorageApplyOutcome(dataset, true, currentIndex, writeAlias, templateName, policyName, null);
        }
        catch (Exception exception) when (exception is ElasticsearchRequestException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            return new StorageApplyOutcome(dataset, false, currentIndex, writeAlias, templateName, policyName, exception.Message);
        }
    }

    public async Task<IReadOnlyList<ElasticsearchIndexInfo>> ListDatasetIndicesAsync(StorageSettingsSnapshot settings, string dataset, CancellationToken cancellationToken = default)
    {
        var client = await RequireClientAsync(cancellationToken).ConfigureAwait(false);
        var pattern = StorageIndexNaming.Pattern(settings.IndexPrefix, dataset);
        return await client.ListIndicesAsync(pattern, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CountAsync(StorageSettingsSnapshot settings, string dataset, JsonObject? query = null, CancellationToken cancellationToken = default)
    {
        var client = await RequireClientAsync(cancellationToken).ConfigureAwait(false);
        var pattern = StorageIndexNaming.Pattern(settings.IndexPrefix, dataset);
        return await client.CountAsync(pattern, query, cancellationToken).ConfigureAwait(false);
    }

    // Deletes whole periods that fell out of the retention window. Only used when ILM is off for
    // the dataset — with ILM on, the cluster deletes the same indices through the policy's delete
    // phase, and doing both would make the settings page lie about which mechanism removed them.
    public async Task<(int Deleted, long Documents, IReadOnlyList<string> Names)> DeleteExpiredAsync(
        string dataset,
        StorageSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        var datasetSettings = settings.For(dataset);
        if (datasetSettings.RetentionDays <= 0)
        {
            return (0, 0, Array.Empty<string>());
        }

        var client = await RequireClientAsync(cancellationToken).ConfigureAwait(false);
        var indices = await ListDatasetIndicesAsync(settings, dataset, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var deleted = 0;
        var documents = 0L;
        var names = new List<string>();

        foreach (var index in indices)
        {
            var period = StorageIndexNaming.PeriodOf(settings.IndexPrefix, dataset, index.Name);
            if (period == null || !StorageIndexNaming.IsExpired(period.Value, datasetSettings.RetentionDays, now))
            {
                continue;
            }

            try
            {
                await client.DeleteIndexAsync(index.Name, cancellationToken).ConfigureAwait(false);
                deleted++;
                documents += index.DocumentCount;
                names.Add(index.Name);
            }
            catch (ElasticsearchRequestException)
            {
                // A concurrent retention pass on the other host may have removed it already.
            }
        }

        return (deleted, documents, names);
    }

    public static IReadOnlyList<string> DescribeExpiredPlan(StorageSettingsSnapshot settings, string dataset, IReadOnlyList<ElasticsearchIndexInfo> indices, DateTime nowUtc)
    {
        var datasetSettings = settings.For(dataset);
        if (datasetSettings.RetentionDays <= 0)
        {
            return Array.Empty<string>();
        }

        return indices
            .Select(index => (Index: index, Period: StorageIndexNaming.PeriodOf(settings.IndexPrefix, dataset, index.Name)))
            .Where(item => item.Period != null && StorageIndexNaming.IsExpired(item.Period.Value, datasetSettings.RetentionDays, nowUtc))
            .Select(item => item.Index.Name)
            .ToList();
    }

    private async Task<ElasticsearchClient> RequireClientAsync(CancellationToken cancellationToken)
    {
        var resolved = await _clients.GetAsync(cancellationToken).ConfigureAwait(false);
        if (resolved == null)
        {
            throw new InvalidOperationException("Elasticsearch is not configured or is switched off; nothing was sent to a cluster.");
        }

        return resolved.Value.Client;
    }
}
