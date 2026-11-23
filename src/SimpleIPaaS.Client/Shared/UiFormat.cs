using System;
using System.Linq;

namespace SimpleIPaaS.Client.Shared;

/// <summary>
/// Presentation helpers shared by the integration pages. They live in one place because the
/// list and the detail view must format the same values identically (status tones, relative
/// times, avatar monograms).
/// </summary>
public static class UiFormat
{
    private static readonly string[] AvatarTones = { string.Empty, "tone-teal", "tone-violet", "tone-cyan", "tone-pink", "tone-amber" };

    public static bool IsActiveRun(string? status) =>
        status != null &&
        (status.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("InProgress", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("Running", StringComparison.OrdinalIgnoreCase));

    public static bool IsProblemRun(string? status) =>
        status != null &&
        (status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("PartialSuccess", StringComparison.OrdinalIgnoreCase));

    /// <summary>CSS class for the shared status badge (the app colours badges by lowercase status).</summary>
    public static string StatusBadgeClass(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "queued" : status.Trim().ToLowerInvariant();

    /// <summary>CSS class for the chip variants used in tables and lists.</summary>
    public static string RunChipClass(string? status) => (status ?? string.Empty).ToLowerInvariant() switch
    {
        "success" or "recovered" => "chip-success",
        "failed" => "chip-danger",
        "partialsuccess" or "retrying" => "chip-warning",
        "inprogress" or "running" or "queued" => "chip-primary",
        _ => string.Empty
    };

    public static string RunRailClass(string? status) => (status ?? string.Empty).ToLowerInvariant() switch
    {
        "success" or "recovered" => "is-success",
        "failed" => "is-failed",
        "partialsuccess" or "retrying" => "is-warning",
        "inprogress" or "running" or "queued" => "is-running",
        _ => string.Empty
    };

    public static string RelativeTime(DateTime utc)
    {
        var value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var delta = DateTime.UtcNow - value;
        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        return value.ToLocalTime().ToString("MMM d, yyyy");
    }

    public static string DurationLabel(DateTime startedAt, DateTime? completedAt)
    {
        if (!completedAt.HasValue)
        {
            return "running";
        }

        var span = completedAt.Value - startedAt;
        if (span.TotalMilliseconds < 0) return "-";
        if (span.TotalSeconds < 1) return $"{span.TotalMilliseconds:F0} ms";
        if (span.TotalSeconds < 60) return $"{span.TotalSeconds:F1} s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }

    public static string Monogram(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var parts = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
        }

        var trimmed = name.Trim();
        return trimmed.Length >= 2 ? trimmed[..2].ToUpperInvariant() : trimmed.ToUpperInvariant();
    }

    public static string AvatarTone(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var hash = name.Aggregate(0, (acc, ch) => acc + ch);
        return AvatarTones[hash % AvatarTones.Length];
    }

    public static string ShortId(Guid id) => id == Guid.Empty ? "-" : $"{id.ToString()[..8]}...";

    public static string FlowCountLabel(int count) => count == 1 ? "1 flow" : $"{count} flows";

    public static string StepCountLabel(int count) => count == 1 ? "1 step" : $"{count} steps";

    /// <summary>Human label for a flow trigger, including the cron expression when there is one.</summary>
    public static string TriggerLabel(string? triggerType, string? cronExpression)
    {
        var trigger = string.IsNullOrWhiteSpace(triggerType) ? "Manual" : triggerType.Trim();
        if (trigger.Equals("Cron", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cronExpression))
        {
            return $"Cron {cronExpression.Trim()}";
        }

        return trigger;
    }
}
