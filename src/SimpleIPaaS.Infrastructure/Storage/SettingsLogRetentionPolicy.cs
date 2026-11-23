using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Logging;

namespace SimpleIPaaS.Infrastructure.Storage;

// Effective retention windows for the relational log table.
//
// Configured on the settings page as one number of days for the logs dataset, applied to every log
// level: an operator who asks for three days gets three days, rather than discovering that warnings
// quietly follow a different rule. 0 means "keep everything by age"; the row cap stays as a safety
// valve so a runaway log loop cannot fill the disk either way.
public sealed record LogRetentionWindows(int DetailRetentionHours, int WarningRetentionHours, int MaxRows);

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

        var days = snapshot.For(StorageDatasets.Logs).RetentionDays;
        if (days <= 0)
        {
            return fallback with { DetailRetentionHours = 0, WarningRetentionHours = 0 };
        }

        var hours = days * 24;
        return fallback with { DetailRetentionHours = hours, WarningRetentionHours = hours };
    }
}
