using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Logging;

namespace SimpleIPaaS.Infrastructure.Storage;

// Effective retention windows for the relational log table.
//
// Configured on the settings page as two numbers of days for the logs dataset: one for warnings and errors, one for
// every other level. 0 keeps that part forever. A row saved before the warnings setting existed applies the logs
// window to both. Logs leave by age only: nothing is removed because of how many there are (the log volume alert
// emails instead).
public sealed record LogRetentionWindows(int DetailRetentionHours, int WarningRetentionHours);

public sealed class SettingsLogRetentionPolicy : ILogRetentionPolicy
{
    private readonly IStorageSettingsProvider _settings;

    public SettingsLogRetentionPolicy(IStorageSettingsProvider settings)
    {
        _settings = settings;
    }

    public LogRetentionWindows Resolve(LogRetentionWindows fallback)
    {
        if (!_settings.TryGetCached(out var snapshot))
        {
            return fallback;
        }

        var logs = snapshot.For(StorageDatasets.Logs);
        return new LogRetentionWindows(
            Math.Max(0, logs.RetentionDays) * 24,
            Math.Max(0, StorageCatalog.LogWarningRetentionDays(logs)) * 24);
    }
}
