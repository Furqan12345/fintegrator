using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
using SimpleIPaaS.Domain.Entities;
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

            // Everything logged inside this scope carries the correlation ids, so the
            // database log sink can populate its ExecutionId/FlowId/TenantId columns and
            // the Debug Logs console can filter a whole run by execution.
            using var executionActivity = new Activity("flow.execution");
            executionActivity.SetIdFormat(ActivityIdFormat.W3C);
            if (!string.IsNullOrWhiteSpace(claim.TraceParent)) executionActivity.SetParentId(claim.TraceParent);
            executionActivity.Start();

            using var correlationScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["ExecutionId"] = claim.ExecutionId,
                ["FlowId"] = claim.FlowId,
                ["FlowName"] = claim.FlowName,
                ["IntegrationId"] = claim.IntegrationId ?? Guid.Empty,
                ["IntegrationName"] = claim.IntegrationName,
                ["TenantId"] = claim.TenantId,
                ["IsTest"] = claim.IsTest,
                ["TestCaseId"] = claim.TestCaseId,
                ["TestRunId"] = claim.TestRunId
            });

            var claimedAt = Stopwatch.GetTimestamp();
            _logger.LogInformation(new EventId(1200, "flow.execution.claimed"),
                "Claimed queued execution {ExecutionId} for flow {FlowId} (tenant {TenantId}, trigger {TriggerSource}, queue wait {QueueWaitMs}ms)",
                claim.ExecutionId, claim.FlowId, claim.TenantId, string.IsNullOrWhiteSpace(claim.TriggerSource) ? "Manual" : claim.TriggerSource, claim.QueuedAt.HasValue ? (long)Math.Max(0, (DateTime.UtcNow - claim.QueuedAt.Value).TotalMilliseconds) : 0);

            try
            {
                await ExecuteRequestAsync(claim, stoppingToken);
                _logger.LogInformation(
                    "Released execution {ExecutionId} for flow {FlowId} after {DurationMs}ms",
                    claim.ExecutionId, claim.FlowId, (long)Stopwatch.GetElapsedTime(claimedAt).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Execution {ExecutionId} abandoned after {DurationMs}ms because the Engine is shutting down",
                    claim.ExecutionId, (long)Stopwatch.GetElapsedTime(claimedAt).TotalMilliseconds);
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
        var lockWaitStartedAt = Stopwatch.GetTimestamp();
        await flowLock.WaitAsync(stoppingToken);
        var lockWaitMs = (long)Stopwatch.GetElapsedTime(lockWaitStartedAt).TotalMilliseconds;
        if (lockWaitMs > 0)
        {
            _logger.LogInformation(new EventId(1210, "flow.lock.waited"), "Flow lock wait for execution {ExecutionId} was {LockWaitMs}ms", request.ExecutionId, lockWaitMs);
        }

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

            FlowTestContext? testContext = null;
            FlowTestRun? testRun = null;
            FlowTestCase? testCase = null;
            var testRepository = scope.ServiceProvider.GetRequiredService<IFlowTestRepository>();
            if (execution.IsTest)
            {
                testContext = FlowTestContext.FromJson(execution.TestDefinitionJson, execution.Id);
                if (execution.TestRunId is Guid testRunId)
                {
                    testRun = await testRepository.GetRunAsync(testRunId);
                    if (testRun != null)
                    {
                        testRun.Status = "Running";
                        testRun.StartedAt = DateTime.UtcNow;
                        await testRepository.UpdateRunAsync(testRun);
                    }
                }
                if (execution.TestCaseId is Guid testCaseId)
                {
                    testCase = await testRepository.GetAsync(testCaseId);
                }
            }

            var executor = scope.ServiceProvider.GetRequiredService<FlowExecutor>();
            var startedAt = Stopwatch.GetTimestamp();
            var result = await executor.ExecuteFlowAsync(request.FlowId, request.ExecutionId, request.TriggerPayload, linked.Token, testContext);
            var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (testRun != null)
            {
                var steps = await executionRepository.GetStepExecutionsAsync(execution.Id);
                var evaluation = FlowTestEvaluator.Evaluate(result, steps, testContext!.Definition);
                testRun.Status = evaluation.Passed ? "Passed" : "Failed";
                testRun.ResultJson = evaluation.ToJson();
                testRun.ErrorMessage = evaluation.Passed ? null : string.Join(" ", evaluation.Failures);
                testRun.CompletedAt = DateTime.UtcNow;
                await testRepository.UpdateRunAsync(testRun);

                if (testCase != null)
                {
                    testCase.LastRunAt = testRun.CompletedAt;
                    if (evaluation.Passed) testCase.LastPassedAt = testRun.CompletedAt;
                    await testRepository.UpdateAsync(testCase);
                }

                _logger.LogInformation("Test case {TestCaseId} {TestStatus} for execution {ExecutionId}: {TestResult}",
                    testRun.TestCaseId, testRun.Status, execution.Id, testRun.ErrorMessage ?? "all assertions passed");
            }

            if (result.Status == ExecutionStatus.Failed)
            {
                _logger.LogError(
                    "Execution {ExecutionId} finished with status {Status} in {DurationMs}ms ({SuccessRecords} succeeded, {FailedRecords} failed): {ErrorMessage}",
                    request.ExecutionId, result.Status, elapsedMs, result.SuccessRecords, result.FailedRecords, result.ErrorMessage);
            }
            else
            {
                _logger.LogInformation(
                    "Execution {ExecutionId} finished with status {Status} in {DurationMs}ms ({SuccessRecords} succeeded, {FailedRecords} failed)",
                    request.ExecutionId, result.Status, elapsedMs, result.SuccessRecords, result.FailedRecords);
            }
        }
        finally
        {
            flowLock.Release();
        }
    }
}
