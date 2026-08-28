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

namespace SimpleIPaaS.Engine.Workers;

// Lives exclusively in the Engine: the UI only saves Schedule-node / trigger data
// onto the IntegrationFlow row; this scanner is what actually turns due flows into
// Queued FlowExecutions by writing through FlowRunService.
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
        var scheduleRepository = scanScope.ServiceProvider.GetRequiredService<ICronScheduleRepository>();
        var flows = await repository.GetActiveCronFlowsAcrossTenantsAsync();
        _logger.LogDebug("Cron scan evaluating {FlowCount} scheduled flow(s)", flows.Count());

        foreach (var flow in flows)
        {
            stoppingToken.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow;

            if (flow.RunAt != null)
            {
                if (flow.NextRunAt == null || flow.NextRunAt > now)
                {
                    continue;
                }

                _logger.LogInformation(
                    "One-time schedule fired for flow {FlowId} '{FlowName}' (was due {DueAt:u})",
                    flow.Id, flow.Name, flow.NextRunAt);

                // Clear the marker before enqueueing — at-most-once dispatch.
                await scheduleRepository.UpdateNextRunAtAsync(flow.Id, flow.TenantId, null);
                await EnqueueAsync(flow.Id, flow.TenantId, "Schedule", stoppingToken);
                continue;
            }

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

            if (flow.NextRunAt == null)
            {
                var next = expression.GetNextOccurrence(now);
                _logger.LogInformation(
                    "Primed cron schedule '{CronExpression}' for flow {FlowId} '{FlowName}'; first run at {NextRunAt:u}",
                    flow.CronExpression, flow.Id, flow.Name, next);
                await scheduleRepository.UpdateNextRunAtAsync(flow.Id, flow.TenantId, next);
                continue;
            }

            if (flow.NextRunAt > now)
            {
                continue;
            }

            var nextRun = expression.GetNextOccurrence(now);
            _logger.LogInformation(
                "Cron '{CronExpression}' fired for flow {FlowId} '{FlowName}' (due {DueAt:u}); next run at {NextRunAt:u}",
                flow.CronExpression, flow.Id, flow.Name, flow.NextRunAt, nextRun);

            await EnqueueAsync(flow.Id, flow.TenantId, "Cron", stoppingToken);
            await scheduleRepository.UpdateNextRunAtAsync(flow.Id, flow.TenantId, nextRun);
        }
    }

    private async Task EnqueueAsync(Guid flowId, Guid tenantId, string triggerSource, CancellationToken stoppingToken)
    {
        try
        {
            using var runScope = _serviceProvider.CreateScope();
            var tenantContext = runScope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenantContext.SetTenantId(tenantId);

            var runService = runScope.ServiceProvider.GetRequiredService<FlowRunService>();
            var executionId = await runService.EnqueueAsync(flowId, tenantId, triggerSource, null, stoppingToken);
            _logger.LogInformation("{TriggerSource} trigger enqueued execution {ExecutionId} for flow {FlowId}", triggerSource, executionId, flowId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue {TriggerSource} execution for flow {FlowId}", triggerSource, flowId);
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
