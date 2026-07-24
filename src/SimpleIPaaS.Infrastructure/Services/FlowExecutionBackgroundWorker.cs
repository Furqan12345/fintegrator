using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Services;

public class FlowExecutionBackgroundWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FlowExecutionBackgroundWorker> _logger;

    public FlowExecutionBackgroundWorker(IServiceProvider serviceProvider, ILogger<FlowExecutionBackgroundWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Dead letter worker is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred processing dead letters");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken stoppingToken)
    {
        using var scanScope = _serviceProvider.CreateScope();
        var executionRepository = scanScope.ServiceProvider.GetRequiredService<IExecutionRepository>();
        var pending = await executionRepository.GetPendingDeadLettersAcrossTenantsAsync(100);

        foreach (var entry in pending)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                tenantContext.SetTenantId(entry.TenantId);

                var deadLetterService = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
                await deadLetterService.ReplayEntryAsync(entry.Id, force: false, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error replaying dead letter {DeadLetterId}", entry.Id);
            }
        }
    }
}
