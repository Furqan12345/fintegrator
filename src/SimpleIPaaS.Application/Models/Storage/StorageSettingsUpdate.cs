using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleIPaaS.Application.Models.Storage;

// What the settings page submits. Secret fields are null when the operator did not retype them —
// "blank keeps the stored credential" is enforced here rather than in the UI.
public sealed class StorageSettingsUpdate
{
    public bool ElasticsearchEnabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    public bool ClearPassword { get; set; }

    public string? ApiKey { get; set; }

    public bool ClearApiKey { get; set; }

    public string IndexPrefix { get; set; } = "fusetor";

    public bool AllowInvalidCertificate { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    public IReadOnlyList<StorageDatasetSettings> Datasets { get; set; } = Array.Empty<StorageDatasetSettings>();

    public static StorageSettingsUpdate FromSnapshot(StorageSettingsSnapshot snapshot)
    {
        var update = new StorageSettingsUpdate
        {
            ElasticsearchEnabled = snapshot.ElasticsearchEnabled,
            Url = snapshot.Url,
            Username = snapshot.Username,
            IndexPrefix = snapshot.IndexPrefix,
            AllowInvalidCertificate = snapshot.AllowInvalidCertificate,
            TimeoutSeconds = snapshot.TimeoutSeconds,
            Datasets = StorageDatasets.All.Select(dataset => snapshot.For(dataset).Clone()).ToList()
        };

        return update;
    }
}
