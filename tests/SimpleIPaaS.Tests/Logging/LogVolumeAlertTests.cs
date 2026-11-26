using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Logging;

// The log volume alert replaced the row cap: it emails, it never deletes. And the logs have two windows.
public sealed class LogVolumeAlertTests
{
    private sealed class FixedProbe(long stored) : ILogVolumeProbe
    {
        public long Stored { get; set; } = stored;
        public int Breakdowns { get; private set; }

        public Task<LogVolume> MeasureAsync(bool includeBreakdown, CancellationToken cancellationToken = default)
        {
            if (includeBreakdown) Breakdowns++;
            return Task.FromResult(new LogVolume(
                Stored, 2_500_000_000, Stored / 2,
                includeBreakdown ? new[] { new LogVolumeSource("Engine.HttpStep", Stored / 3) } : Array.Empty<LogVolumeSource>(),
                includeBreakdown ? new[] { new LogVolumeSource("Shopify <sync>", Stored / 4) } : Array.Empty<LogVolumeSource>()));
        }
    }

    private static AlertSettings Alerts(int level = 0, DateTime? sentAt = null) =>
        new() { Recipients = "ops@example.com; owner@example.com", LogVolumeAlertLevel = level, LogVolumeAlertSentAt = sentAt };

    [Theory]
    [InlineData(0, 1000, LogVolumeLevel.Normal)]
    [InlineData(799, 1000, LogVolumeLevel.Normal)]
    [InlineData(800, 1000, LogVolumeLevel.Approaching)]
    [InlineData(999, 1000, LogVolumeLevel.Approaching)]
    [InlineData(1000, 1000, LogVolumeLevel.Reached)]
    [InlineData(5_000_000, 1000, LogVolumeLevel.Reached)]
    [InlineData(5_000_000, 0, LogVolumeLevel.Normal)]
    public void Level_is_approaching_at_80_percent_and_reached_at_the_threshold(long stored, long alertRows, LogVolumeLevel expected) =>
        Assert.Equal(expected, LogVolumeAlertService.LevelFor(stored, alertRows));

    [Fact]
    public async Task Rising_to_approaching_and_then_reached_emails_each_time_and_a_steady_level_waits_a_day()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var probe = new FixedProbe(850);
        var email = new FakeEmailSender();
        var service = new LogVolumeAlertService(probe, email, NullLogger<LogVolumeAlertService>.Instance);

        var first = await service.CheckAsync(1000, 3, 14, Alerts(), now);
        Assert.Equal(LogVolumeLevel.Approaching, first.Level);
        Assert.True(first.EmailSent && first.Persist);
        Assert.Equal(2, email.Messages.Count);
        Assert.Contains("85% of the alert limit", email.Messages[0].Subject);

        // Same level an hour later: no second email.
        var steady = await service.CheckAsync(1000, 3, 14, Alerts(1, now), now.AddHours(1));
        Assert.False(steady.EmailSent);
        Assert.False(steady.Persist);
        Assert.Equal(2, email.Messages.Count);

        // Passing the threshold emails at once.
        probe.Stored = 1_200;
        var reached = await service.CheckAsync(1000, 3, 14, Alerts(1, now), now.AddHours(2));
        Assert.Equal(LogVolumeLevel.Reached, reached.Level);
        Assert.True(reached.EmailSent);
        Assert.Contains("passed the alert limit", email.Messages[^1].Subject);

        // Still over a day later: a reminder.
        var reminder = await service.CheckAsync(1000, 3, 14, Alerts(2, now.AddHours(2)), now.AddHours(26));
        Assert.True(reminder.EmailSent);

        // Back to normal: recorded without an email, so the next rise emails again.
        probe.Stored = 100;
        var normal = await service.CheckAsync(1000, 3, 14, Alerts(2, now.AddHours(26)), now.AddHours(27));
        Assert.Equal(LogVolumeLevel.Normal, normal.Level);
        Assert.False(normal.EmailSent);
        Assert.True(normal.Persist);
    }

    [Fact]
    public async Task Without_recipients_or_with_the_alert_off_nothing_is_sent_and_a_failed_send_is_retried()
    {
        var now = DateTime.UtcNow;
        var email = new FakeEmailSender();
        var service = new LogVolumeAlertService(new FixedProbe(5_000), email, NullLogger<LogVolumeAlertService>.Instance);

        Assert.False((await service.CheckAsync(1000, 3, 14, null, now)).EmailSent);
        Assert.False((await service.CheckAsync(1000, 3, 14, new AlertSettings { Recipients = "" }, now)).EmailSent);
        Assert.Equal(LogVolumeLevel.Normal, (await service.CheckAsync(0, 3, 14, Alerts(), now)).Level);
        Assert.Empty(email.Messages);

        email.ThrowOnSend = new InvalidOperationException("smtp down");
        var failed = await service.CheckAsync(1000, 3, 14, Alerts(), now);
        Assert.False(failed.EmailSent);
        Assert.False(failed.Persist); // nothing recorded: the next check tries again
    }

    [Fact]
    public void The_email_names_what_logged_most_says_nothing_was_deleted_and_encodes_names()
    {
        var volume = new LogVolume(1_200_000, 2_500_000_000, 600_000,
            new[] { new LogVolumeSource("Engine.HttpStep", 400_000) },
            new[] { new LogVolumeSource("Shopify <sync>", 300_000) });

        var email = LogVolumeAlertService.BuildEmail(volume, 1_000_000, LogVolumeLevel.Reached, 3, 14, "https://fusetor.example.com/");

        Assert.Equal("Fusetor: stored logs passed the alert limit (1,200,000 of 1,000,000 lines)", email.Subject);
        Assert.Contains("Engine.HttpStep: 400,000 lines (66%)", email.TextBody);
        Assert.Contains("Shopify <sync>: 300,000 lines (50%)", email.TextBody);
        Assert.Contains("Shopify &lt;sync&gt;", email.HtmlBody);
        Assert.Contains("2.3 GB", email.TextBody);
        Assert.Contains("3 days for most lines, 14 days for warnings and errors", email.TextBody);
        Assert.Contains("Nothing has been deleted because of this limit", email.TextBody);
        Assert.Contains("https://fusetor.example.com/settings", email.TextBody);
    }

    [Fact]
    public void Settings_saved_before_the_split_keep_warnings_on_the_logs_window_and_new_installs_get_14_days()
    {
        var legacy = StorageCatalog.Parse("[{\"Dataset\":\"logs\",\"RetentionDays\":7}]").First(item => item.Dataset == StorageDatasets.Logs);
        Assert.Null(legacy.WarningRetentionDays);
        Assert.Equal(7, StorageCatalog.LogWarningRetentionDays(legacy));
        Assert.Equal(StorageCatalog.DefaultLogAlertRows, StorageCatalog.LogAlertRows(legacy));

        var fresh = StorageCatalog.Defaults().First(item => item.Dataset == StorageDatasets.Logs);
        Assert.Equal(3, fresh.RetentionDays);
        Assert.Equal(14, StorageCatalog.LogWarningRetentionDays(fresh));
        Assert.Equal(1_000_000, StorageCatalog.LogAlertRows(fresh));

        // An upgraded install that never saved settings keeps one window, as before.
        var previous = StorageCatalog.PreviousDefaults().First(item => item.Dataset == StorageDatasets.Logs);
        Assert.Equal(3, StorageCatalog.LogWarningRetentionDays(previous));

        // Elasticsearch keeps every level in one index: the longer window, and 0 (forever) wins.
        Assert.Equal(14, StorageCatalog.IndexRetentionDays(fresh));
        Assert.Equal(0, StorageCatalog.IndexRetentionDays(new StorageDatasetSettings { Dataset = StorageDatasets.Logs, RetentionDays = 3, WarningRetentionDays = 0 }));
        Assert.Equal(30, StorageCatalog.IndexRetentionDays(new StorageDatasetSettings { Dataset = StorageDatasets.Executions, RetentionDays = 30, WarningRetentionDays = 90 }));
    }

    [Theory]
    [InlineData(3, 14, true)]
    [InlineData(3, 3, true)]
    [InlineData(3, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(7, 3, false)]
    [InlineData(0, 14, false)]
    public void Warnings_are_never_kept_for_less_time_than_the_other_logs(int days, int warningDays, bool valid) =>
        Assert.Equal(valid, StorageCatalog.ValidateLogWindows(days, warningDays) == null);

    [Fact]
    public void The_log_writer_gets_both_windows_from_the_settings_page()
    {
        var snapshot = new StorageSettingsSnapshot
        {
            Datasets = new Dictionary<string, StorageDatasetSettings>(StringComparer.OrdinalIgnoreCase)
            {
                [StorageDatasets.Logs] = new() { Dataset = StorageDatasets.Logs, RetentionDays = 2, WarningRetentionDays = 30 }
            }
        };
        var windows = new SettingsLogRetentionPolicy(new StubStorageSettingsProvider(snapshot)).Resolve(new LogRetentionWindows(72, 336));
        Assert.Equal(new LogRetentionWindows(48, 720), windows);
    }

    [Fact]
    public async Task The_probe_counts_the_stored_lines_and_what_logged_most_in_the_last_day()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var context = new IPaaSContext(new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var flowId = Guid.NewGuid();
        context.AppLogEntries.AddRange(Enumerable.Range(0, 6).Select(i => new AppLogEntry
        {
            Timestamp = i < 5 ? DateTime.UtcNow : DateTime.UtcNow.AddDays(-3),
            Level = "Information",
            Source = "Engine",
            Category = i < 4 ? "Noisy.Worker" : "Quiet",
            Message = "line",
            FlowId = i < 3 ? flowId : null,
            FlowName = i < 3 ? "Busy flow" : null,
        }));
        await context.SaveChangesAsync();

        var volume = await new RelationalLogVolumeProbe(context).MeasureAsync(includeBreakdown: true);

        Assert.Equal(6, volume.StoredLines);
        Assert.Equal(5, volume.LastDayLines);
        Assert.Null(volume.StoredBytes);
        Assert.Equal(new LogVolumeSource("Noisy.Worker", 4), volume.TopCategories[0]);
        Assert.Equal(new LogVolumeSource("Busy flow", 3), Assert.Single(volume.TopFlows));
    }
}
