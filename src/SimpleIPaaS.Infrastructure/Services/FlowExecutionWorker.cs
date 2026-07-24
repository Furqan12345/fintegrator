using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Services;

public class FlowExecutionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IExecutionQueue _queue;
    private readonly ExecutionCancellationRegistry _cancellationRegistry;
    private readonly ExecutionOptions _options;
    private readonly ILogger<FlowExecutionWorker> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _flowLocks = new();

    public FlowExecutionWorker(
        IServiceProvider serviceProvider,
        IExecutionQueue queue,
        ExecutionCancellationRegistry cancellationRegistry,
        IOptions<ExecutionOptions> options,
        ILogger<FlowExecutionWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _queue = queue;
        _cancellationRegistry = cancellationRegistry;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = _options.MaxConcurrency < 1 ? 4 : _options.MaxConcurrency;
        _logger.LogInformation("FlowExecutionWorker starting with {MaxConcurrency} workers", concurrency);

        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => ProcessQueueAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ExecutionRequest request;
            try
            {
                request = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await ExecuteRequestAsync(request, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error running execution {ExecutionId}", request.ExecutionId);
            }
            finally
            {
                _cancellationRegistry.Remove(request.ExecutionId);
            }
        }
    }

    private async Task ExecuteRequestAsync(ExecutionRequest request, CancellationToken stoppingToken)
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
