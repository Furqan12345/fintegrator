using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Engine.Workers;

// Claims Queued FlowExecution rows straight from the shared database — the exact
// counterpart of FlowRunService.EnqueueAsync writes performed by the API process.
public class FlowExecutionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ExecutionCancellationRegistry _cancellationRegistry;
    private readonly ExecutionOptions _options;
    private readonly ILogger<FlowExecutionWorker> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _flowLocks = new();

    public FlowExecutionWorker(
        IServiceProvider serviceProvider,
        ExecutionCancellationRegistry cancellationRegistry,
        IOptions<ExecutionOptions> options,
        ILogger<FlowExecutionWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _cancellationRegistry = cancellationRegistry;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = _options.MaxConcurrency < 1 ? 4 : _options.MaxConcurrency;
        var pollInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds < 1 ? 2 : _options.PollIntervalSeconds);
        _logger.LogInformation(
            "FlowExecutionWorker starting with {MaxConcurrency} workers, polling the database every {PollSeconds}s",
            concurrency, pollInterval.TotalSeconds);

        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => RunLoopAsync(pollInterval, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task RunLoopAsync(TimeSpan pollInterval, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            QueuedExecutionClaim? claim = null;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                claim = await scope.ServiceProvider.GetRequiredService<IExecutionRepository>()
                    .TryClaimNextQueuedExecutionAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SqliteException ex)
            {
                // Write contention with the API process under WAL is transient by design.
                _logger.LogDebug(ex, "SQLite busy while claiming the next queued execution");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error claiming the next queued execution");
            }

            if (claim == null)
            {
                try
                {
                    await Task.Delay(pollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await ExecuteRequestAsync(claim, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error running execution {ExecutionId}", claim.ExecutionId);
            }
            finally
            {
                _cancellationRegistry.Remove(claim.ExecutionId);
            }
        }
    }

    private async Task ExecuteRequestAsync(QueuedExecutionClaim request, CancellationToken stoppingToken)
    {
        var flowLock = _flowLocks.GetOrAdd(request.FlowId, _ => new SemaphoreSlim(1, 1));
        await flowLock.WaitAsync(stoppingToken);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenantContext.SetTenantId(request.TenantId);

            var executionRepository = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();
            var execution = await executionRepository.GetFlowExecutionAsync(request.ExecutionId);
            if (execution == null)
            {
                _logger.LogWarning("Execution {ExecutionId} not found; skipping", request.ExecutionId);
                return;
            }

            var cts = _cancellationRegistry.GetOrCreate(request.ExecutionId);

            if (execution.Status == ExecutionStatus.Cancelled || cts.IsCancellationRequested)
            {
                if (execution.Status != ExecutionStatus.Cancelled)
                {
                    execution.Status = ExecutionStatus.Cancelled;
                    execution.ErrorMessage = "Execution was cancelled before it started.";
                    execution.CompletedAt = DateTime.UtcNow;
                    await executionRepository.UpdateFlowExecutionAsync(execution);
                }

                _logger.LogInformation("Execution {ExecutionId} cancelled before start", request.ExecutionId);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stoppingToken);
            var maxDuration = _options.MaxFlowDurationSeconds < 1 ? 600 : _options.MaxFlowDurationSeconds;
            linked.CancelAfter(TimeSpan.FromSeconds(maxDuration));

            var executor = scope.ServiceProvider.GetRequiredService<FlowExecutor>();
            await executor.ExecuteFlowAsync(request.FlowId, request.ExecutionId, request.TriggerPayload, linked.Token);
        }
        finally
        {
            flowLock.Release();
        }
    }
}
