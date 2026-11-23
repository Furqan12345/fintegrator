using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Engine.Workers;

// Sends one digest email per scan when failures have accumulated. The email configuration
// comes from the shared EmailServiceRegistration, so the API and Engine read the same
// Email:Smtp settings.
public sealed class FailureAlertWorker : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FailureAlertWorker> _logger;

    public FailureAlertWorker(IServiceProvider serviceProvider, ILogger<FailureAlertWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Failure alert worker is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAlertsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing failure alerts");
            }

            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessAlertsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IPaaSContext>();

        var settings = await context.AlertSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Enabled, stoppingToken);
        if (settings == null)
        {
            return;
        }

        var now = DateTime.UtcNow;

        try
        {
            var service = scope.ServiceProvider.GetRequiredService<FailureAlertService>();
            var alerted = await service.ProcessAsync(settings, now, stoppingToken);

            if (alerted > 0)
            {
                await context.AlertSettings
                    .Where(row => row.Id == settings.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(row => row.LastSentAt, now),
                        stoppingToken);

                _logger.LogInformation(
                    "Failure alert digest sent: {Count} failed order(s).", alerted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failure alert scan failed; it will be retried.");
        }
    }
}
