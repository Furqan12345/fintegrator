using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

// Storage settings: which backend serves each growing dataset, how long the data is kept, and
// where the Elasticsearch cluster lives. Secrets are never echoed back — only "one is stored".
public sealed class StorageSettingsDto
{
    public bool ElasticsearchEnabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public bool HasPassword { get; set; }

    public bool HasApiKey { get; set; }

    public string IndexPrefix { get; set; } = "fusetor";

    public bool AllowInvalidCertificate { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    public DateTime? UpdatedAt { get; set; }

    public List<StorageDatasetSettingsDto> Datasets { get; set; } = new();

    // Labels, descriptions and defaults so the page can render its matrix without hard-coding
    // anything that the server already knows.
    public List<StorageDatasetCatalogDto> Catalog { get; set; } = new();
}

public sealed class StorageDatasetSettingsDto
{
    public string Dataset { get; set; } = string.Empty;

    public bool UseElasticsearch { get; set; }

    // Daily | Weekly | Monthly — how often a new backing index is started.
    public string Granularity { get; set; } = "Daily";

    // 0 keeps data forever.
    public int RetentionDays { get; set; }

    // Elasticsearch only: let the ILM policy delete expired indices instead of the application.
    public bool UseIlm { get; set; } = true;
}

public sealed class StorageDatasetCatalogDto
{
    public string Dataset { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Holds { get; set; } = string.Empty;

    public string VolumeHint { get; set; } = string.Empty;

    public bool CanUseElasticsearch { get; set; }

    public bool CanUseIlm { get; set; }

    public string SqlStore { get; set; } = string.Empty;

    public string DefaultGranularity { get; set; } = "Daily";

    public int DefaultRetentionDays { get; set; }

    public bool DefaultUseIlm { get; set; } = true;
}

public sealed class StorageSettingsUpdateDto
{
    public bool ElasticsearchEnabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    // Leave blank to keep the stored credential; set the Clear flags to remove it.
    public string? Password { get; set; }

    public bool ClearPassword { get; set; }

    public string? ApiKey { get; set; }

    public bool ClearApiKey { get; set; }

    public string IndexPrefix { get; set; } = "fusetor";

    public bool AllowInvalidCertificate { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    public List<StorageDatasetSettingsDto> Datasets { get; set; } = new();
}

public sealed class StorageSaveResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public StorageSettingsDto Settings { get; set; } = new();

    public List<StorageApplyOutcomeDto> Applied { get; set; } = new();
}

public sealed class StorageConnectionTestDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? ClusterName { get; set; }

    public string? Version { get; set; }

    public int DurationMs { get; set; }
}

// Optional body for POST /api/storage/test: the values currently in the settings form. They are
// tested as submitted and never persisted, so an unsaved URL can be validated before saving it.
// A blank password/API key falls back to the stored secret (the same "blank keeps the current"
// rule the save endpoint uses).
public sealed class StorageConnectionTestRequestDto
{
    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    public string? ApiKey { get; set; }

    public bool AllowInvalidCertificate { get; set; }

    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class StorageInventoryDto
{
    public DateTime CollectedAtUtc { get; set; }

    public string? Error { get; set; }

    public List<StorageDatasetStatusDto> Datasets { get; set; } = new();
}

public sealed class StorageDatasetStatusDto
{
    public string Dataset { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Backend { get; set; } = "Database";

    public bool CanUseElasticsearch { get; set; }

    public bool UseIlm { get; set; }

    public int RetentionDays { get; set; }

    public string SqlStore { get; set; } = string.Empty;

    public long DocumentCount { get; set; }

    public string WriteAlias { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    public string TemplateName { get; set; } = string.Empty;

    public string PolicyName { get; set; } = string.Empty;

    public bool Ready { get; set; }

    public string? Error { get; set; }

    public List<StorageIndexDto> Indices { get; set; } = new();
}

public sealed class StorageIndexDto
{
    public string Name { get; set; } = string.Empty;

    public long DocumentCount { get; set; }

    public string Size { get; set; } = "0";

    public DateTime? CreatedAtUtc { get; set; }

    public int AgeDays { get; set; }

    // True when the retention window has passed: ILM will delete it, or the app already can.
    public bool Expired { get; set; }
}

public sealed class StorageApplyResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<StorageApplyOutcomeDto> Datasets { get; set; } = new();
}

public sealed class StorageApplyOutcomeDto
{
    public string Dataset { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string Index { get; set; } = string.Empty;

    public string WriteAlias { get; set; } = string.Empty;

    public string TemplateName { get; set; } = string.Empty;

    public string PolicyName { get; set; } = string.Empty;

    public string? Error { get; set; }
}

public sealed class StorageRetentionReportDto
{
    public DateTime RanAtUtc { get; set; }

    public List<StorageRetentionOutcomeDto> Datasets { get; set; } = new();
}

public sealed class StorageRetentionOutcomeDto
{
    public string Dataset { get; set; } = string.Empty;

    public string Backend { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public long Removed { get; set; }

    public string? Error { get; set; }
}
