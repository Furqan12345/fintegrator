using System;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Services;

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
        _logger.LogInformation("FlowExecutionBackgroundWorker is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var deadLetterService = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
                
                await deadLetterService.ProcessPendingLettersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred processing dead letters");
            }

            // Wait 1 minute before checking again
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
