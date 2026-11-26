using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public enum LogVolumeLevel
{
    Normal = 0,
    Approaching = 1,
    Reached = 2
}

// The result of one check. Persist is true when the stored alert state (level, last email) changed.
public sealed record LogVolumeCheck(LogVolumeLevel Level, long StoredLines, bool EmailSent, bool Persist, DateTime? SentAt);

// The log volume alert: the stored log lines are compared with the alert threshold (the former row cap). It only
// tells people; nothing is deleted because of it, logs still leave only by age.
//  * Approaching at 80% of the threshold, Reached at 100%.
//  * An email goes to the failure-alert recipients when the level rises, and again every 24 hours while it stays
//    up. Going back down is recorded without an email, so a later rise emails again.
public sealed class LogVolumeAlertService
{
    public const double ApproachingShare = 0.8;
    public static readonly TimeSpan Reminder = TimeSpan.FromHours(24);

    private readonly ILogVolumeProbe _probe;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<LogVolumeAlertService> _logger;
    private readonly string _publicBaseUrl;

    public LogVolumeAlertService(
        ILogVolumeProbe probe,
        IEmailSender emailSender,
        ILogger<LogVolumeAlertService> logger,
        IConfiguration? configuration = null)
    {
        _probe = probe;
        _emailSender = emailSender;
        _logger = logger;
        _publicBaseUrl = (configuration?["App:PublicBaseUrl"] ?? string.Empty).Trim();
    }

    public static LogVolumeLevel LevelFor(long storedLines, long alertRows) =>
        alertRows <= 0 ? LogVolumeLevel.Normal
        : storedLines >= alertRows ? LogVolumeLevel.Reached
        : storedLines >= (long)Math.Ceiling(alertRows * ApproachingShare) ? LogVolumeLevel.Approaching
        : LogVolumeLevel.Normal;

    public static bool ShouldEmail(LogVolumeLevel level, LogVolumeLevel previous, DateTime? lastSentAt, DateTime now) =>
        level > LogVolumeLevel.Normal && (level > previous || lastSentAt is null || now - lastSentAt.Value >= Reminder);

    // alerts may be null (no alert settings row yet: nobody to email). detailDays/warningDays are the log windows,
    // quoted in the email so the reader knows what still removes logs.
    public async Task<LogVolumeCheck> CheckAsync(
        long alertRows,
        int detailDays,
        int warningDays,
        AlertSettings? alerts,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var previous = (LogVolumeLevel)Math.Clamp(alerts?.LogVolumeAlertLevel ?? 0, 0, 2);
        var lastSentAt = alerts?.LogVolumeAlertSentAt;
        if (alertRows <= 0)
        {
            return new LogVolumeCheck(LogVolumeLevel.Normal, 0, false, alerts != null && previous != LogVolumeLevel.Normal, lastSentAt);
        }

        var volume = await _probe.MeasureAsync(includeBreakdown: false, cancellationToken).ConfigureAwait(false);
        var level = LevelFor(volume.StoredLines, alertRows);
        var recipients = FailureAlertService.ParseRecipients(alerts?.Recipients);

        if (ShouldEmail(level, previous, lastSentAt, now) && recipients.Count > 0)
        {
            var detailed = await _probe.MeasureAsync(includeBreakdown: true, cancellationToken).ConfigureAwait(false);
            var email = BuildEmail(detailed, alertRows, level, detailDays, warningDays, _publicBaseUrl);
            try
            {
                foreach (var recipient in recipients)
                {
                    await _emailSender.SendAsync(recipient, email.Subject, email.HtmlBody, email.TextBody, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Nothing is recorded, so the next check tries again.
                _logger.LogWarning(exception, "Could not send the log volume alert; it will be retried.");
                return new LogVolumeCheck(level, volume.StoredLines, false, false, lastSentAt);
            }

            return new LogVolumeCheck(level, volume.StoredLines, true, true, now);
        }

        // A lower level is recorded (no email), so rising again emails again.
        return new LogVolumeCheck(level, volume.StoredLines, false, alerts != null && level < previous, lastSentAt);
    }

    public static FailureAlertEmail BuildEmail(
        LogVolume volume, long alertRows, LogVolumeLevel level, int detailDays, int warningDays, string? publicBaseUrl)
    {
        var baseUrl = (publicBaseUrl ?? string.Empty).TrimEnd('/');
        var percent = alertRows > 0 ? volume.StoredLines * 100 / alertRows : 0;
        var subject = level == LogVolumeLevel.Reached
            ? $"Fusetor: stored logs passed the alert limit ({Number(volume.StoredLines)} of {Number(alertRows)} lines)"
            : $"Fusetor: stored logs are at {percent}% of the alert limit ({Number(volume.StoredLines)} of {Number(alertRows)} lines)";

        var facts = new List<string>
        {
            $"Stored log lines: {Number(volume.StoredLines)} (alert limit {Number(alertRows)})",
            $"Logged in the last 24 hours: {Number(volume.LastDayLines)} {(volume.LastDayLines == 1 ? "line" : "lines")}"
        };
        if (volume.StoredBytes is { } bytes)
        {
            facts.Add($"Space used by the logs: {Size(bytes)}");
        }

        facts.Add($"Logs are removed by age only: {Window(detailDays)} for most lines, {Window(warningDays)} for warnings and errors.");

        var html = new StringBuilder();
        var text = new StringBuilder();
        html.Append("<p><strong>").Append(Encode(subject)).Append("</strong></p>");
        text.AppendLine(subject).AppendLine();

        html.Append("<ul>");
        foreach (var fact in facts)
        {
            html.Append("<li>").Append(Encode(fact)).Append("</li>");
            text.Append("- ").AppendLine(fact);
        }

        html.Append("</ul>");
        text.AppendLine();

        AppendSources(html, text, "Most lines in the last 24 hours came from", volume.TopCategories, volume.LastDayLines);
        AppendSources(html, text, "Flows that logged the most in the last 24 hours", volume.TopFlows, volume.LastDayLines);

        const string Nothing = "Nothing has been deleted because of this limit; it only sends this email.";
        const string Advice = "To bring the volume down: fix the flow or component that logs the most, raise its minimum log level " +
                              "(Logging:LogLevel in appsettings.json), or shorten the log windows in Settings → Storage. " +
                              "To change the limit, edit \"Alert when stored log lines reach\" there.";
        html.Append("<p>").Append(Encode(Nothing)).Append("</p><p>").Append(Encode(Advice)).Append("</p>");
        text.AppendLine(Nothing).AppendLine(Advice);

        if (!string.IsNullOrEmpty(baseUrl))
        {
            html.Append("<p><a href=\"").Append(Encode(baseUrl + "/logs")).Append("\">Open Debug Logs</a> · <a href=\"")
                .Append(Encode(baseUrl + "/settings")).Append("\">Open Settings</a></p>");
            text.AppendLine().Append("Debug Logs: ").AppendLine(baseUrl + "/logs").Append("Settings: ").AppendLine(baseUrl + "/settings");
        }

        const string Footer = "You're receiving this because you are a failure-alert recipient in Settings → Alerts.";
        html.Append("<p style=\"color:#666\">").Append(Encode(Footer)).Append("</p>");
        text.AppendLine().AppendLine(Footer);
        return new FailureAlertEmail(subject, html.ToString(), text.ToString());
    }

    private static void AppendSources(StringBuilder html, StringBuilder text, string heading, IReadOnlyList<LogVolumeSource> sources, long total)
    {
        if (sources.Count == 0)
        {
            return;
        }

        html.Append("<p>").Append(Encode(heading)).Append(":</p><ul>");
        text.AppendLine(heading + ":");
        foreach (var source in sources)
        {
            var share = total > 0 ? $" ({Math.Min(100, source.Lines * 100 / total)}%)" : string.Empty;
            var line = $"{source.Name}: {Number(source.Lines)} {(source.Lines == 1 ? "line" : "lines")}{share}";
            html.Append("<li>").Append(Encode(line)).Append("</li>");
            text.Append("- ").AppendLine(line);
        }

        html.Append("</ul>");
        text.AppendLine();
    }

    private static string Window(int days) => days <= 0 ? "kept forever" : days == 1 ? "1 day" : $"{days} days";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Size(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
