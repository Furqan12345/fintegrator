using System;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Services;

public class CronTriggerScheduler : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CronTriggerScheduler> _logger;

    public CronTriggerScheduler(IServiceProvider serviceProvider, ILogger<CronTriggerScheduler> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CronTriggerScheduler starting (scan interval {ScanInterval}s)", ScanInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning cron-triggered flows");
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

    private async Task ScanAsync(CancellationToken stoppingToken)
    {
        using var scanScope = _serviceProvider.CreateScope();
        var repository = scanScope.ServiceProvider.GetRequiredService<IIntegrationRepository>();
        var flows = await repository.GetActiveCronFlowsAcrossTenantsAsync();

        foreach (var flow in flows)
        {
            stoppingToken.ThrowIfCancellationRequested();

            CronExpression expression;
            try
            {
                expression = ParseCronExpression(flow.CronExpression);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flow {FlowId} has an invalid cron expression '{CronExpression}'", flow.Id, flow.CronExpression);
                continue;
            }

            var now = DateTime.UtcNow;

            if (flow.NextRunAt == null)
            {
                var next = expression.GetNextOccurrence(now);
                await repository.UpdateNextRunAtAsync(flow.Id, next);
                continue;
            }

            if (flow.NextRunAt > now)
            {
                continue;
            }

            try
            {
                using var runScope = _serviceProvider.CreateScope();
                var tenantContext = runScope.ServiceProvider.GetRequiredService<ITenantContext>();
                tenantContext.SetTenantId(flow.TenantId);

                var runService = runScope.ServiceProvider.GetRequiredService<FlowRunService>();
                var executionId = await runService.EnqueueAsync(flow.Id, flow.TenantId, "Cron", null, stoppingToken);
                _logger.LogInformation("Cron trigger enqueued execution {ExecutionId} for flow {FlowId}", executionId, flow.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue cron execution for flow {FlowId}", flow.Id);
            }

            var nextRun = expression.GetNextOccurrence(now);
            await repository.UpdateNextRunAtAsync(flow.Id, nextRun);
        }
    }

    private static CronExpression ParseCronExpression(string expression)
    {
        try
        {
            return CronExpression.Parse(expression);
        }
        catch (CronFormatException)
        {
            return CronExpression.Parse(expression, CronFormat.IncludeSeconds);
        }
    }
}
