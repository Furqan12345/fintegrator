using System;

namespace SimpleIPaaS.Shared.Models;

public sealed class AlertSettingsDto
{
    public bool Enabled { get; set; }
    public string Recipients { get; set; } = string.Empty;
    public int CooldownMinutes { get; set; } = 15;
    public DateTime? EnabledAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // False when Email:Smtp:Host is blank, so the settings page can warn before the
    // owner tries to save or test and gets a server-side error.
    public bool SmtpConfigured { get; set; }
}

public sealed class AlertSettingsUpdateDto
{
    public bool Enabled { get; set; }
    public string Recipients { get; set; } = string.Empty;
    public int CooldownMinutes { get; set; } = 15;
}
