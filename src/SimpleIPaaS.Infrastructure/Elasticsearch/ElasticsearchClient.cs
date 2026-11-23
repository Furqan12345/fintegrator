using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// A small Elasticsearch REST client.
//
// Deliberately hand-rolled over HttpClient instead of taking a client dependency: this app needs
// exact control over index templates, ILM policies and write-alias bootstrapping (which client
// libraries model inconsistently across versions), it must work against Elasticsearch 7, 8 and 9,
// and every request has to be assertable in tests and in the request log of the mock cluster.
//
// The client is stateless apart from its HttpClient, so one instance can serve every dataset.
public sealed class ElasticsearchClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly ElasticsearchOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public ElasticsearchClient(ElasticsearchOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new HttpClientHandler();
            if (options.AllowInvalidCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300)) };
            _ownsHttpClient = true;
        }
    }

    public ElasticsearchOptions Options => _options;

    public async Task<ElasticsearchClusterInfo> PingAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/", null, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        var version = body?["version"]?["number"]?.GetValue<string>() ?? "unknown";
        var nodeName = body?["name"]?.GetValue<string>() ?? "unknown";
        var clusterName = body?["cluster_name"]?.GetValue<string>() ?? "unknown";
        var product = response.Headers.TryGetValues("X-Elastic-Product", out var values) ? values.FirstOrDefault() : null;

        return new ElasticsearchClusterInfo(nodeName, clusterName, version, product);
    }

    public Task<bool> IndexExistsAsync(string index, CancellationToken cancellationToken = default) =>
        ExistsAsync($"/{Uri.EscapeDataString(index)}", cancellationToken);

    public Task<bool> IndexTemplateExistsAsync(string name, CancellationToken cancellationToken = default) =>
        ExistsAsync($"/_index_template/{Uri.EscapeDataString(name)}", cancellationToken);

    public Task<bool> IlmPolicyExistsAsync(string name, CancellationToken cancellationToken = default) =>
        ExistsAsync($"/_ilm/policy/{Uri.EscapeDataString(name)}", cancellationToken);

    public async Task CreateIndexAsync(string index, JsonObject body, CancellationToken cancellationToken = default)
    {
        using var response = await SendJsonAsync(HttpMethod.Put, $"/{Uri.EscapeDataString(index)}", body, cancellationToken).ConfigureAwait(false);
        await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"/{Uri.EscapeDataString(index)}", null, cancellationToken, tolerateMissing: true).ConfigureAwait(false);
        await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAliasesAsync(JsonArray actions, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["actions"] = actions };
        using var response = await SendJsonAsync(HttpMethod.Post, "/_aliases", body, cancellationToken).ConfigureAwait(false);
        await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task PutIndexTemplateAsync(string name, JsonObject body, CancellationToken cancellationToken = default)
    {
        using var response = await SendJsonAsync(HttpMethod.Put, $"/_index_template/{Uri.EscapeDataString(name)}", body, cancellationToken).ConfigureAwait(false);
        await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task PutIlmPolicyAsync(string name, JsonObject body, CancellationToken cancellationToken = default)
    {
        using var response = await SendJsonAsync(HttpMethod.Put, $"/_ilm/policy/{Uri.EscapeDataString(name)}", body, cancellationToken).ConfigureAwait(false);
        await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // <pattern> is an index pattern such as "fusetor-logs-*"; allow_no_indices keeps a
    // dataset that has no index yet from turning into an error.
    public async Task<long> CountAsync(string pattern, JsonObject? query, CancellationToken cancellationToken = default)
    {
        var path = $"/{PathSegment(pattern)}/_count?ignore_unavailable=true&allow_no_indices=true";

        // The query node is cloned because a caller may legitimately use the same filter for a count
        // and for the search that follows, and a JsonNode can only ever have one parent.
        var body = query == null ? null : new JsonObject { ["query"] = query.DeepClone() };
        using var response = await SendJsonAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
        var payload = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return payload?["count"]?.GetValue<long>() ?? 0L;
    }

    public async Task<long> DeleteByQueryAsync(string pattern, JsonObject query, bool refresh, CancellationToken cancellationToken = default)
    {
        var path = $"/{PathSegment(pattern)}/_delete_by_query?ignore_unavailable=true&allow_no_indices=true&conflicts=proceed&refresh={(refresh ? "true" : "false")}";
        var body = new JsonObject { ["query"] = query.DeepClone() };
        using var response = await SendJsonAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
        var payload = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return payload?["deleted"]?.GetValue<long>() ?? 0L;
    }

    public async Task<ElasticsearchSearchResult> SearchAsync(string pattern, JsonObject body, CancellationToken cancellationToken = default)
    {
        var path = $"/{PathSegment(pattern)}/_search?ignore_unavailable=true&allow_no_indices=true";

        // Cloned for the same reason as the count body: a JsonNode can only have one parent, and a
        // caller may legitimately reuse a filter across several searches (the facets aggregation
        // does exactly that).
        using var response = await SendJsonAsync(HttpMethod.Post, path, body.DeepClone(), cancellationToken).ConfigureAwait(false);
        var payload = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        var hits = new List<ElasticsearchHit>();
        long total = 0;
        var hitsNode = payload?["hits"];
        if (hitsNode != null)
        {
            var totalNode = hitsNode["total"];
            if (totalNode is JsonObject totalObject)
            {
                total = totalObject["value"]?.GetValue<long>() ?? 0L;
            }
            else if (totalNode != null)
            {
                total = totalNode.GetValue<long>();
            }

            if (hitsNode["hits"] is JsonArray array)
            {
                foreach (var hit in array.OfType<JsonObject>())
                {
                    hits.Add(new ElasticsearchHit(
                        hit["_id"]?.GetValue<string>() ?? string.Empty,
                        hit["_index"]?.GetValue<string>() ?? string.Empty,
                        hit["_source"]));
                }
            }
        }

        return new ElasticsearchSearchResult(total, hits, payload?["aggregations"]);
    }

    // Realtime multi-get by document id â€” used for the cross-reference "has this key been seen?"
    // lookup, where a stale search response would cause duplicate work.
    public async Task<IReadOnlyDictionary<string, JsonNode?>> MultiGetAsync(string pattern, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return found;
        }

        var body = new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(id => (JsonNode)id!).ToArray())
        };

        var path = $"/{Uri.EscapeDataString(pattern)}/_mget?ignore_unavailable=true&allow_no_indices=true";
        using var response = await SendJsonAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
        var payload = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        if (payload?["docs"] is JsonArray docs)
        {
            foreach (var doc in docs.OfType<JsonObject>())
            {
                var id = doc["_id"]?.GetValue<string>();
                if (id == null)
                {
                    continue;
                }

                var isFound = doc["found"]?.GetValue<bool>() ?? false;
                found[id] = isFound ? doc["_source"] : null;
            }
        }

        return found;
    }

    public async Task<ElasticsearchBulkResult> BulkAsync(string target, IReadOnlyCollection<ElasticsearchBulkOperation> operations, CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0)
        {
            return new ElasticsearchBulkResult(0, false, Array.Empty<ElasticsearchBulkItem>());
        }

        var builder = new StringBuilder();
        foreach (var operation in operations)
        {
            var action = operation.Kind switch
            {
                ElasticsearchBulkOperationKind.Create => "create",
                ElasticsearchBulkOperationKind.Delete => "delete",
                _ => "index"
            };

            var meta = new JsonObject { ["_index"] = target, ["_id"] = operation.Id };
            builder.Append(new JsonObject { [action] = meta }.ToJsonString(SerializerOptions));
            builder.Append('\n');

            if (operation.Kind != ElasticsearchBulkOperationKind.Delete)
            {
                builder.Append((operation.Document ?? new JsonObject()).ToJsonString(SerializerOptions));
                builder.Append('\n');
            }
        }

        var path = $"/{Uri.EscapeDataString(target)}/_bulk?ignore_unavailable=true&allow_no_indices=true";
        using var content = new StringContent(builder.ToString(), Encoding.UTF8, "application/x-ndjson");
        using var response = await SendAsync(HttpMethod.Post, path, content, cancellationToken).ConfigureAwait(false);
        var payload = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        var items = new List<ElasticsearchBulkItem>();
        if (payload?["items"] is JsonArray itemArray)
        {
            foreach (var item in itemArray.OfType<JsonObject>())
            {
                var entry = item.FirstOrDefault();
                var detail = entry.Value as JsonObject;
                if (detail == null)
                {
                    continue;
                }

                items.Add(new ElasticsearchBulkItem(
                    entry.Key,
                    detail["_id"]?.GetValue<string>(),
                    detail["_index"]?.GetValue<string>(),
                    (int)(detail["status"]?.GetValue<double>() ?? 0d),
                    detail["error"]?["type"]?.GetValue<string>(),
                    detail["error"]?["reason"]?.GetValue<string>()));
            }
        }

        return new ElasticsearchBulkResult(
            (int)(payload?["took"]?.GetValue<double>() ?? 0d),
            payload?["errors"]?.GetValue<bool>() ?? false,
            items);
    }

    public async Task<IReadOnlyList<ElasticsearchIndexInfo>> ListIndicesAsync(string pattern, CancellationToken cancellationToken = default)
    {
        var path = $"/_cat/indices/{PathSegment(pattern)}?format=json&bytes=b&h=index,health,status,docs.count,store.size,creation.date";
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken, tolerateMissing: true).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<ElasticsearchIndexInfo>();
        }

        var payload = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (payload is not JsonArray rows)
        {
            return Array.Empty<ElasticsearchIndexInfo>();
        }

        var indices = new List<ElasticsearchIndexInfo>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            var name = row["index"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            indices.Add(new ElasticsearchIndexInfo(
                name,
                ParseLong(row["docs.count"]),
                ParseString(row["store.size"]) ?? "0",
                ParseEpochMillis(row["creation.date"])));
        }

        return indices.OrderBy(index => index.Name, StringComparer.Ordinal).ToList();
    }

    // Index names and patterns this app builds only ever contain [a-z0-9-] plus a wildcard, so they
    // are sent as-is: escaping the "*" would make the pattern unreadable in the cluster's own logs
    // and in the request log of the mock cluster used for verification.
    internal static string PathSegment(string value) => value;

    private static long ParseLong(JsonNode? node)
    {
        if (node == null)
        {
            return 0L;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var parsed))
            {
                return parsed;
            }

            if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var fromText))
            {
                return fromText;
            }
        }

        return 0L;
    }

    // _cat columns are strings in Elasticsearch but numbers in the mock (and through some proxies),
    // so both are accepted.
    private static string? ParseString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return value.TryGetValue<double>(out var asDouble) ? asDouble.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static DateTime? ParseEpochMillis(JsonNode? node)
    {
        var millis = ParseLong(node);
        return millis <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
    }

    private async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Head, path, null, cancellationToken, tolerateMissing: true).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, bool tolerateMissing = false) =>
        SendAsync(
            method,
            path,
            body == null ? null : new StringContent(body.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json"),
            cancellationToken,
            tolerateMissing);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, bool tolerateMissing = false)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("Elasticsearch is not configured; set the cluster URL in Settings before using it.");
        }

        using var request = new HttpRequestMessage(method, BuildUri(path));
        if (content != null)
        {
            request.Content = content;
        }

        ApplyAuthentication(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new ElasticsearchRequestException(method.Method, path, 0, Unwrap(exception), "connection_failed");
        }

        if (response.IsSuccessStatusCode || (tolerateMissing && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var reason = string.Empty;
        string? errorType = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var payload = JsonNode.Parse(text);
                    var error = payload?["error"];
                    errorType = error?["type"]?.GetValue<string>() ?? (error is JsonValue ? error.GetValue<string>() : null);
                    reason = error?["reason"]?.GetValue<string>()
                        ?? error?["root_cause"]?[0]?["reason"]?.GetValue<string>()
                        ?? error?.ToJsonString()
                        ?? text;
                }
                catch (JsonException)
                {
                    reason = text;
                }
            }
        }
        catch (Exception)
        {
            // The status code alone is enough to report the failure.
        }

        response.Dispose();
        throw new ElasticsearchRequestException(method.Method, path, status, Truncate(reason), errorType);
    }

    private static string Unwrap(Exception exception) =>
        exception is TaskCanceledException ? "the request timed out" : exception.Message;

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "â€¦";

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        if (_options.UsesApiKey)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {_options.ApiKey}");
            return;
        }

        if (!string.IsNullOrEmpty(_options.Username))
        {
            var raw = $"{_options.Username}:{_options.Password}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
    }

    private Uri BuildUri(string path)
    {
        var baseUrl = _options.Url.TrimEnd('/');
        if (!baseUrl.Contains("://", StringComparison.Ordinal))
        {
            baseUrl = "http://" + baseUrl;
        }

        return new Uri(baseUrl + path, UriKind.Absolute);
    }

    private static async Task<JsonNode?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content == null)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // A 200 that is not JSON (a reverse proxy's HTML page, for instance) is a
            // configuration problem worth surfacing rather than an empty success.
            throw new ElasticsearchRequestException(response.RequestMessage?.Method.Method ?? "GET", response.RequestMessage?.RequestUri?.PathAndQuery ?? "/", (int)response.StatusCode, "the response body was not JSON", "invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
