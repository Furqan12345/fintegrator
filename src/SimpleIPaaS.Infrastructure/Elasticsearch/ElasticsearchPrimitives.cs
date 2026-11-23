using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Connection details for one Elasticsearch cluster, resolved from StorageSettings.
public sealed record ElasticsearchOptions(
    string Url,
    string Username,
    string Password,
    string ApiKey,
    string IndexPrefix,
    bool AllowInvalidCertificate,
    int TimeoutSeconds)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

    public bool UsesApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    // Safe for logs and error messages.
    public string Describe() =>
        IsConfigured
            ? $"{Url} (auth: {(UsesApiKey ? "api key" : string.IsNullOrEmpty(Username) ? "none" : "basic")})"
            : "not configured";

    public static ElasticsearchOptions Disabled { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, "fusetor", false, 15);
}

// Any non-2xx answer from the cluster. The message is assembled from the cluster's own error
// payload, which never contains the credentials this client sent.
public sealed class ElasticsearchRequestException : Exception
{
    public ElasticsearchRequestException(string method, string path, int statusCode, string reason, string? errorType = null)
        : base($"{method} {path} failed with HTTP {statusCode}{(string.IsNullOrWhiteSpace(errorType) ? string.Empty : $" ({errorType})")}: {reason}")
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        Reason = reason;
        ErrorType = errorType;
    }

    public string Method { get; }

    public string Path { get; }

    public int StatusCode { get; }

    public string Reason { get; }

    public string? ErrorType { get; }

    public bool IsNotFound => StatusCode == 404;
}

public sealed record ElasticsearchClusterInfo(string NodeName, string ClusterName, string Version, string? Product);

public sealed record ElasticsearchIndexInfo(string Name, long DocumentCount, string Size, DateTime? CreatedAtUtc);

public sealed record ElasticsearchHit(string Id, string Index, JsonNode? Source);

public sealed record ElasticsearchSearchResult(long Total, IReadOnlyList<ElasticsearchHit> Hits, JsonNode? Aggregations = null);

public sealed record ElasticsearchBulkItem(string Kind, string? Id, string? Index, int Status, string? ErrorType, string? ErrorReason)
{
    public bool Succeeded => Status is >= 200 and < 300;

    public bool Conflict => Status == 409;

    public bool IsInsert => Kind is "create" or "index";
}

public sealed record ElasticsearchBulkResult(int Took, bool Errors, IReadOnlyList<ElasticsearchBulkItem> Items)
{
    // Documents actually written. A "create" that hits an existing document is not a write — that is
    // what the cross-reference store counts as "already stored".
    public int Created => Items.Count(item => item.Succeeded && item.IsInsert);

    public int Conflicts => Items.Count(item => item.Conflict);

    public int Failures => Items.Count(item => !item.Succeeded && !item.Conflict);
}

// A single operation inside a bulk request.
public sealed record ElasticsearchBulkOperation(ElasticsearchBulkOperationKind Kind, string Id, JsonNode? Document)
{
    public static ElasticsearchBulkOperation Create(string id, JsonNode document) => new(ElasticsearchBulkOperationKind.Create, id, document);

    public static ElasticsearchBulkOperation Index(string id, JsonNode document) => new(ElasticsearchBulkOperationKind.Index, id, document);

    public static ElasticsearchBulkOperation Delete(string id) => new(ElasticsearchBulkOperationKind.Delete, id, null);
}

public enum ElasticsearchBulkOperationKind
{
    Create,
    Index,
    Delete
}
