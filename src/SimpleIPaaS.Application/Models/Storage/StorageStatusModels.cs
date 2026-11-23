using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Application.Models.Storage;

public sealed record StorageConnectionTestResult(
    bool Success,
    string Message,
    string? ClusterName = null,
    string? Version = null,
    string? Product = null,
    int DurationMs = 0);

// Overrides for a connection test that must not be saved: the settings page posts the values
// currently in its form. A null password/API key means "use the stored secret".
public sealed record StorageConnectionTestRequest(
    string Url,
    string Username,
    string? Password,
    string? ApiKey,
    bool AllowInvalidCertificate,
    int TimeoutSeconds);

// The logs (or another Elasticsearch-backed) endpoint cannot serve a request because the cluster
// client is not usable right now. Callers answer 503 with this message instead of an unhandled 500.
public sealed class StorageBackendUnavailableException : Exception
{
    public StorageBackendUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

// One backing index of a dataset (<prefix>-<dataset>-<period>) as Elasticsearch reports it.
public sealed record StorageIndexInfo(
    string Name,
    long DocumentCount,
    string Size,
    DateTime? CreatedAtUtc,
    int AgeDays,
    bool Expired);

// Everything the settings page shows for one dataset.
public sealed record StorageDatasetStatus(
    string Dataset,
    string Name,
    string Backend,
    bool CanUseElasticsearch,
    bool UseIlm,
    int RetentionDays,
    string SqlStore,
    long DocumentCount,
    string WriteAlias,
    string Pattern,
    string TemplateName,
    string PolicyName,
    bool Ready,
    string? Error,
    IReadOnlyList<StorageIndexInfo> Indices);

public sealed record StorageInventory(
    DateTime CollectedAtUtc,
    IReadOnlyList<StorageDatasetStatus> Datasets,
    string? Error = null);

public sealed record StorageApplyResult(
    bool Success,
    string Message,
    IReadOnlyList<StorageApplyOutcome> Datasets);

public sealed record StorageApplyOutcome(
    string Dataset,
    bool Success,
    string Index,
    string WriteAlias,
    string TemplateName,
    string PolicyName,
    string? Error);

public sealed record StorageRetentionOutcome(
    string Dataset,
    string Backend,
    string Action,
    long Removed,
    string? Error = null);

public sealed record StorageRetentionRunResult(
    DateTime RanAtUtc,
    IReadOnlyList<StorageRetentionOutcome> Datasets);
