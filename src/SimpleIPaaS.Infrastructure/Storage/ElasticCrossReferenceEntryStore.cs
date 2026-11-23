using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Elasticsearch;

namespace SimpleIPaaS.Infrastructure.Storage;

// Cross-reference entries in Elasticsearch.
//
// Insert-only semantics â€” the whole point of the Cross-Reference Store card â€” come from the
// document id: it is derived from list + key, so a bulk "create" against a key that is already
// there always answers 409 version_conflict, exactly like the unique index does in SQL.
// Nothing is written when the key exists, and the count returned is the number of genuinely new
// keys.
public sealed class ElasticCrossReferenceEntryStore : ICrossReferenceEntryStore
{
    private static readonly string[] SearchableFields = { "key_value", "value_json" };

    private readonly ElasticsearchClientProvider _clients;

    public ElasticCrossReferenceEntryStore(ElasticsearchClientProvider clients)
    {
        _clients = clients;
    }

    public async Task<long> CountAsync(string listName, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var query = ElasticsearchQueryBuilder.Bool(filter: new[] { ListFilter(listName) });
        return await client.CountAsync(Pattern(snapshot), query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, long>> CountByListAsync(CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["size"] = 0,
            ["track_total_hits"] = false,
            ["aggs"] = new JsonObject
            {
                ["lists"] = new JsonObject
                {
                    ["terms"] = new JsonObject { ["field"] = "list_name", ["size"] = 500 }
                }
            }
        };

        var result = await client.SearchAsync(Pattern(snapshot), body, cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (result.Aggregations?["lists"]?["buckets"] is JsonArray buckets)
        {
            foreach (var bucket in buckets.OfType<JsonObject>())
            {
                var key = bucket["key"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(key))
                {
                    counts[key] = bucket["doc_count"]?.GetValue<long>() ?? 0L;
                }
            }
        }

        return counts;
    }

    public async Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var distinct = keys.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return found;
        }

        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);

        // Documents are addressed by their derived id, so "which of these keys exist?" is a
        // realtime multi-get rather than a search â€” a fresh write is visible immediately.
        var idToKey = distinct.ToDictionary(
            key => ElasticsearchDocuments.CrossReferenceDocumentId(listName, key),
            key => key,
            StringComparer.Ordinal);

        var result = await client.MultiGetAsync(Pattern(snapshot), idToKey.Keys.ToList(), cancellationToken).ConfigureAwait(false);
        foreach (var (id, source) in result)
        {
            if (source != null && idToKey.TryGetValue(id, out var key))
            {
                found.Add(key);
            }
        }

        return found;
    }

    public async Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries, CancellationToken cancellationToken = default)
    {
        var distinct = entries
            .Where(entry => !string.IsNullOrEmpty(entry.KeyValue))
            .GroupBy(entry => entry.KeyValue, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (distinct.Count == 0)
        {
            return 0;
        }

        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var alias = StorageIndexNaming.WriteAlias(snapshot.IndexPrefix, StorageDatasets.CrossReferences);

        var operations = new List<ElasticsearchBulkOperation>(distinct.Count);
        foreach (var entry in distinct)
        {
            entry.ListName = listName;

            operations.Add(ElasticsearchBulkOperation.Create(
                ElasticsearchDocuments.CrossReferenceDocumentId(listName, entry.KeyValue),
                ElasticsearchDocuments.FromCrossReferenceEntry(entry)));
        }

        var result = await client.BulkAsync(alias, operations, cancellationToken).ConfigureAwait(false);

        if (result.Failures > 0)
        {
            throw new InvalidOperationException(
                $"Elasticsearch rejected {result.Failures} cross-reference write(s): {result.Items.FirstOrDefault(item => !item.Succeeded && !item.Conflict)?.ErrorReason ?? "unknown error"}");
        }

        // Conflicts are the "key already stored" case, which the caller counts as skipped.
        return result.Created;
    }

    public async Task<CrossReferenceEntryPage> GetEntriesAsync(string listName, string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 50 : Math.Min(pageSize, 200);

        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var pattern = Pattern(snapshot);

        var filters = new List<JsonObject> { ListFilter(listName) };
        if (!string.IsNullOrWhiteSpace(search))
        {
            filters.Add(ElasticsearchQueryBuilder.SearchAcross(SearchableFields, search.Trim()));
        }

        var query = ElasticsearchQueryBuilder.Bool(filter: filters);
        var total = await client.CountAsync(pattern, query, cancellationToken).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["query"] = query,
            ["from"] = (page - 1) * pageSize,
            ["size"] = pageSize,
            ["sort"] = ElasticsearchQueryBuilder.Sorts(ElasticsearchQueryBuilder.SortDescending("created_at"))
        };

        var result = await client.SearchAsync(pattern, body, cancellationToken).ConfigureAwait(false);
        var entries = result.Hits.Where(hit => hit.Source != null)
            .Select(hit => ElasticsearchDocuments.ToCrossReferenceEntry(hit.Source!))
            .ToList();

        return new CrossReferenceEntryPage(entries, (int)Math.Min(total, int.MaxValue));
    }

    public async Task<bool> DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var pattern = Pattern(snapshot);

        // The entry id is not the document id (that is the list+key hash), so the document is
        // located first. This is a user-driven single-entry delete, not a hot path.
        var body = new JsonObject
        {
            ["query"] = ElasticsearchQueryBuilder.Bool(filter: new[]
            {
                ElasticsearchQueryBuilder.Term("entry_id", id.ToString())
            }),
            ["size"] = 1
        };

        var result = await client.SearchAsync(pattern, body, cancellationToken).ConfigureAwait(false);
        var hit = result.Hits.FirstOrDefault();
        if (hit == null)
        {
            return false;
        }

        var deleted = await client.BulkAsync(
            pattern,
            new[] { ElasticsearchBulkOperation.Delete(hit.Id) },
            cancellationToken).ConfigureAwait(false);

        return deleted.Items.Any(item => item.Succeeded);
    }

    public async Task<int> ClearListAsync(string listName, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var query = ElasticsearchQueryBuilder.Bool(filter: new[] { ListFilter(listName) });
        var deleted = await client.DeleteByQueryAsync(Pattern(snapshot), query, refresh: true, cancellationToken).ConfigureAwait(false);
        return (int)Math.Min(deleted, int.MaxValue);
    }

    private static JsonObject ListFilter(string listName) => ElasticsearchQueryBuilder.Term("list_name", listName);

    private async Task<(ElasticsearchClient Client, StorageSettingsSnapshot Snapshot)> RequireAsync(CancellationToken cancellationToken)
    {
        var resolved = await _clients.GetAsync(cancellationToken).ConfigureAwait(false);
        if (resolved == null)
        {
            throw new InvalidOperationException("The cross-reference dataset is configured for Elasticsearch, but no cluster is reachable or configured.");
        }

        return (resolved.Value.Client, resolved.Value.Snapshot);
    }

    private static string Pattern(StorageSettingsSnapshot snapshot) =>
        StorageIndexNaming.Pattern(snapshot.IndexPrefix, StorageDatasets.CrossReferences);
}
