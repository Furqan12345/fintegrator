using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;

namespace SimpleIPaaS.Engine.Workers;

// Cross-process cancellation bridge. The API writes Status=Cancelled into the shared
// database; this watcher mirrors those rows into the local cancellation registry so
// FlowExecutor's token fires exactly as an in-process cancel used to.
public class DbCancellationWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _serviceProvider;
    private readonly ExecutionCancellationRegistry _cancellationRegistry;
    private readonly ILogger<DbCancellationWatcher> _logger;

    public DbCancellationWatcher(
        IServiceProvider serviceProvider,
        ExecutionCancellationRegistry cancellationRegistry,
        ILogger<DbCancellationWatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cancellation watcher starting (poll every {Seconds}s)", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error watching for cancelled executions");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WatchOnceAsync(CancellationToken cancellationToken)
    {
        var activeIds = _cancellationRegistry.ActiveIds;
        if (activeIds.Count == 0)
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();
        var cancelled = await repository.GetCancelledExecutionIdsAsync(activeIds);

        foreach (var id in cancelled.Where(id => _cancellationRegistry.Cancel(id)))
        {
            _logger.LogInformation("Execution {ExecutionId} was cancelled through the database", id);
        }
    }
}
