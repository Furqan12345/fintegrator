using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Read side of the logs dataset when it lives in Elasticsearch. Every filter, ordering and cursor
// rule mirrors LogRepository (the relational implementation) so the Debug Logs console behaves
// identically against either backend.
public sealed class ElasticsearchLogRepository : ILogRepository
{
    private static readonly string[] SearchableFields =
    {
        "message", "category", "node_name", "flow_name", "integration_name", "event_name", "component", "operation", "exception"
    };

    private readonly ElasticsearchClientProvider _clients;

    public ElasticsearchLogRepository(ElasticsearchClientProvider clients)
    {
        _clients = clients;
    }

    public async Task<LogPage> GetPageAsync(LogQuery query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 50 : (pageSize > 500 ? 500 : pageSize);

        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var pattern = Pattern(snapshot);
        var filter = BuildQuery(query);

        var total = await client.CountAsync(pattern, filter, cancellationToken).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["query"] = filter,
            ["from"] = (page - 1) * pageSize,
            ["size"] = pageSize,
            ["track_total_hits"] = true,
            ["sort"] = ElasticsearchQueryBuilder.Sorts(ElasticsearchQueryBuilder.SortDescending("sequence"))
        };

        var result = await client.SearchAsync(pattern, body, cancellationToken).ConfigureAwait(false);
        var items = result.Hits.Where(hit => hit.Source != null)
            .Select(hit => ElasticsearchDocuments.ToLogEntry(hit.Source!, hit.Id))
            .ToList();
        var cursor = await GetMaxSequenceAsync(client, pattern, cancellationToken).ConfigureAwait(false);

        return new LogPage(items, page, pageSize, (int)Math.Min(total, int.MaxValue), cursor);
    }

    public async Task<LogTail> GetTailAsync(long afterId, LogQuery query, int limit, CancellationToken cancellationToken = default)
    {
        limit = limit < 1 ? 200 : (limit > 1000 ? 1000 : limit);

        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var pattern = Pattern(snapshot);
        var maxSequence = await GetMaxSequenceAsync(client, pattern, cancellationToken).ConfigureAwait(false);

        // Same rule as the relational store: a cursor ahead of the data means the client is holding
        // a stale cursor (the dataset was cleared), so hand back the newest window instead.
        var reset = afterId > maxSequence;
        var effectiveAfter = reset ? 0L : afterId;

        var clauses = new List<JsonObject> { BuildQuery(query) };
        if (!reset)
        {
            clauses.Add(ElasticsearchQueryBuilder.Range("sequence", gt: effectiveAfter.ToString(CultureInfo.InvariantCulture)));
        }

        var body = new JsonObject
        {
            ["query"] = ElasticsearchQueryBuilder.Bool(filter: clauses),
            ["size"] = limit,
            ["sort"] = ElasticsearchQueryBuilder.Sorts(
                reset
                    ? ElasticsearchQueryBuilder.SortDescending("sequence")
                    : ElasticsearchQueryBuilder.SortAscending("sequence"))
        };

        var result = await client.SearchAsync(pattern, body, cancellationToken).ConfigureAwait(false);
        var entries = result.Hits.Where(hit => hit.Source != null)
            .Select(hit => ElasticsearchDocuments.ToLogEntry(hit.Source!, hit.Id))
            .ToList();

        if (reset)
        {
            entries.Reverse();
        }

        var cursor = entries.Count > 0 ? entries[^1].Id : (reset ? maxSequence : afterId);
        return new LogTail(entries, cursor, reset);
    }

    public async Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var buckets = await AggregateAsync(client, Pattern(snapshot), "sources", "source", 100, null, cancellationToken).ConfigureAwait(false);
        return buckets;
    }

    public async Task<LogFacets> GetFacetsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var pattern = Pattern(snapshot);
        var filter = BuildQuery(query);

        var services = await AggregateAsync(client, pattern, "services", "service", 100, filter, cancellationToken).ConfigureAwait(false);
        var components = await AggregateAsync(client, pattern, "components", "component", 200, filter, cancellationToken).ConfigureAwait(false);
        var events = await AggregateAsync(client, pattern, "events", "event_name", 300, filter, cancellationToken).ConfigureAwait(false);
        var outcomes = await AggregateAsync(client, pattern, "outcomes", "outcome", 50, filter, cancellationToken).ConfigureAwait(false);
        var cardTypes = await AggregateAsync(client, pattern, "card_types", "card_type", 100, filter, cancellationToken).ConfigureAwait(false);

        return new LogFacets(services, components, events, outcomes, cardTypes);
    }

    public async Task<IReadOnlyList<AppLogEntry>> GetExecutionTimelineAsync(Guid executionId, int limit, CancellationToken cancellationToken = default)
    {
        limit = limit < 1 ? 500 : Math.Min(limit, 5000);

        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["query"] = ElasticsearchQueryBuilder.Term("flow_execution_id", executionId.ToString()),
            ["size"] = limit,
            ["sort"] = ElasticsearchQueryBuilder.Sorts(
                ElasticsearchQueryBuilder.SortAscending("timestamp"),
                ElasticsearchQueryBuilder.SortAscending("sequence"))
        };

        var result = await client.SearchAsync(Pattern(snapshot), body, cancellationToken).ConfigureAwait(false);
        return result.Hits.Where(hit => hit.Source != null)
            .Select(hit => ElasticsearchDocuments.ToLogEntry(hit.Source!, hit.Id))
            .ToList();
    }

    public async Task<TelemetryHealthSnapshot> GetTelemetryHealthAsync(CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var pattern = Pattern(snapshot);

        var latestBody = new JsonObject
        {
            ["size"] = 1,
            ["_source"] = new JsonArray("timestamp"),
            ["sort"] = ElasticsearchQueryBuilder.Sorts(ElasticsearchQueryBuilder.SortDescending("timestamp"))
        };

        var latestResult = await client.SearchAsync(pattern, latestBody, cancellationToken).ConfigureAwait(false);
        var latestSource = latestResult.Hits.FirstOrDefault()?.Source;
        DateTime? latestAtUtc = latestSource == null
            ? null
            : ElasticsearchDocuments.ToLogEntry(latestSource).Timestamp;

        var total = await client.CountAsync(pattern, null, cancellationToken).ConfigureAwait(false);

        var recentQuery = ElasticsearchQueryBuilder.Bool(filter: new[]
        {
            ElasticsearchQueryBuilder.Terms("level", new[] { "Error", "Critical" }),
            ElasticsearchQueryBuilder.Range("timestamp", gte: Iso(DateTime.UtcNow.AddMinutes(-15)))
        });

        var recentErrors = await client.CountAsync(pattern, recentQuery, cancellationToken).ConfigureAwait(false);

        return new TelemetryHealthSnapshot(latestAtUtc, total, recentErrors);
    }

    public async Task<int> DeleteAsync(DateTime? olderThanUtc, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var query = olderThanUtc.HasValue
            ? ElasticsearchQueryBuilder.Range("timestamp", lt: Iso(olderThanUtc.Value))
            : ElasticsearchQueryBuilder.MatchAll();

        var deleted = await client.DeleteByQueryAsync(Pattern(snapshot), query, refresh: true, cancellationToken).ConfigureAwait(false);
        return (int)Math.Min(deleted, int.MaxValue);
    }

    private async Task<(ElasticsearchClient Client, StorageSettingsSnapshot Snapshot)> RequireAsync(CancellationToken cancellationToken)
    {
        var resolved = await _clients.GetAsync(cancellationToken).ConfigureAwait(false);
        if (resolved == null)
        {
            // Typed so the API can answer 503 (retryable) instead of an unhandled 500.
            throw new StorageBackendUnavailableException(
                "The logs dataset is on Elasticsearch, but the cluster client is not ready yet (no URL configured or the master switch is off). Retry in a moment.");
        }

        return (resolved.Value.Client, resolved.Value.Snapshot);
    }

    private static string Pattern(StorageSettingsSnapshot snapshot) =>
        StorageIndexNaming.Pattern(snapshot.IndexPrefix, StorageDatasets.Logs);

    private static async Task<long> GetMaxSequenceAsync(ElasticsearchClient client, string pattern, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["size"] = 1,
            ["_source"] = new JsonArray("sequence"),
            ["sort"] = ElasticsearchQueryBuilder.Sorts(ElasticsearchQueryBuilder.SortDescending("sequence"))
        };

        var result = await client.SearchAsync(pattern, body, cancellationToken).ConfigureAwait(false);
        var source = result.Hits.FirstOrDefault()?.Source;
        return source?["sequence"]?.GetValue<long>() ?? 0L;
    }

    private static async Task<IReadOnlyList<string>> AggregateAsync(
        ElasticsearchClient client,
        string pattern,
        string aggregationName,
        string field,
        int size,
        JsonObject? query,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["size"] = 0,
            ["track_total_hits"] = false,
            ["aggs"] = new JsonObject
            {
                [aggregationName] = new JsonObject
                {
                    ["terms"] = new JsonObject
                    {
                        ["field"] = field,
                        ["size"] = size
                    }
                }
            }
        };

        if (query != null)
        {
            // Cloned because the same filter is used for every facet aggregation in one call.
            body["query"] = query.DeepClone();
        }

        var result = await client.SearchAsync(pattern, body, cancellationToken).ConfigureAwait(false);
        var buckets = result.Aggregations?[aggregationName]?["buckets"] as JsonArray;
        if (buckets == null)
        {
            return Array.Empty<string>();
        }

        return buckets
            .OfType<JsonObject>()
            .Select(bucket => bucket["key"]?.GetValue<string>())
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonObject BuildQuery(LogQuery query)
    {
        var filters = new List<JsonObject>();
        var allowedLevels = LogRepository.ExpandMinimumLevel(query.MinimumLevel);
        if (allowedLevels != null)
        {
            filters.Add(ElasticsearchQueryBuilder.Terms("level", allowedLevels));
        }

        AddTerm(filters, "service", query.Service);
        AddTerm(filters, "component", query.Component);
        AddTerm(filters, "event_name", query.EventName);
        AddTerm(filters, "operation", query.Operation);
        AddTerm(filters, "outcome", query.Outcome);
        AddTerm(filters, "source", query.Source);
        AddTerm(filters, "trace_id", query.TraceId);
        AddTerm(filters, "card_id", query.CardId);
        AddTerm(filters, "step_execution_id", query.StepExecutionId);
        AddTerm(filters, "invocation", query.Invocation);
        AddTerm(filters, "retry_attempt", query.RetryAttempt);
        AddTerm(filters, "is_test", query.IsTest);
        AddTerm(filters, "test_case_id", query.TestCaseId);
        AddTerm(filters, "test_run_id", query.TestRunId);
        AddTerm(filters, "integration_id", query.IntegrationId);
        AddTerm(filters, "flow_execution_id", query.FlowExecutionId);
        AddTerm(filters, "flow_id", query.FlowId);

        if (query.SinceUtc.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Range("timestamp", gte: Iso(query.SinceUtc.Value)));
        }

        if (query.UntilUtc.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Range("timestamp", lt: Iso(query.UntilUtc.Value)));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filters.Add(ElasticsearchQueryBuilder.SearchAcross(SearchableFields, query.Search.Trim()));
        }

        if (filters.Count == 0)
        {
            return ElasticsearchQueryBuilder.MatchAll();
        }

        return filters.Count == 1 ? filters[0] : ElasticsearchQueryBuilder.Bool(filter: filters);
    }

    private static void AddTerm(List<JsonObject> filters, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            filters.Add(ElasticsearchQueryBuilder.Term(field, value.Trim()));
        }
    }

    private static void AddTerm(List<JsonObject> filters, string field, Guid? value)
    {
        if (value.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term(field, value.Value.ToString()));
        }
    }

    private static void AddTerm(List<JsonObject> filters, string field, int? value)
    {
        if (value.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term(field, value.Value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void AddTerm(List<JsonObject> filters, string field, bool? value)
    {
        if (value.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term(field, value.Value ? "true" : "false"));
        }
    }

    private static string Iso(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
