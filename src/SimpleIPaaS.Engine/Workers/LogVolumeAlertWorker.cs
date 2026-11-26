using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Engine.Workers;

// Checks the stored log lines against the log volume alert threshold every few minutes and emails the alert
// recipients when they approach or pass it (LogVolumeAlertService). Runs in the Engine only, so one host sends.
// It never deletes logs: they leave by age only.
public sealed class LogVolumeAlertWorker : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LogVolumeAlertWorker> _logger;
    private LogVolumeLevel _lastLoggedLevel = LogVolumeLevel.Normal;

    public LogVolumeAlertWorker(IServiceProvider serviceProvider, ILogger<LogVolumeAlertWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Log volume check failed; it will be retried.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IStorageSettingsProvider>().GetAsync(cancellationToken);
        if (snapshot.UsesElasticsearch(StorageDatasets.Logs))
        {
            // The logs live in Elasticsearch, whose own lifecycle manages them; the relational table is not filling.
            return;
        }

        var logs = snapshot.For(StorageDatasets.Logs);
        var context = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
        var alerts = await context.AlertSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var check = await scope.ServiceProvider.GetRequiredService<LogVolumeAlertService>().CheckAsync(
            StorageCatalog.LogAlertRows(logs),
            logs.RetentionDays,
            StorageCatalog.LogWarningRetentionDays(logs),
            alerts,
            now,
            cancellationToken);

        if (check.Level > _lastLoggedLevel)
        {
            _logger.LogWarning(
                "Stored logs are {Level} the log volume alert: {Lines} lines (alert at {AlertRows}). Nothing is deleted because of it.",
                check.Level == LogVolumeLevel.Reached ? "past" : "approaching", check.StoredLines, StorageCatalog.LogAlertRows(logs));
        }

        _lastLoggedLevel = check.Level;
        if (check.EmailSent)
        {
            _logger.LogInformation("Log volume alert emailed ({Level}, {Lines} stored lines).", check.Level, check.StoredLines);
        }

        if (check.Persist && alerts != null)
        {
            await context.AlertSettings
                .Where(row => row.Id == alerts.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.LogVolumeAlertLevel, (int)check.Level)
                    .SetProperty(row => row.LogVolumeAlertSentAt, check.SentAt), cancellationToken);
        }
    }
}
