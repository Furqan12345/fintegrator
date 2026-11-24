using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
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

namespace SimpleIPaaS.Engine.Workers;

// Claims Queued FlowExecution rows straight from the shared database — the exact
// counterpart of FlowRunService.EnqueueAsync writes performed by the API process.
//
// Lifecycle guarantees:
//  * Runs of one flow never overlap in this Engine, and the claim skips flows that are already running here, so
//    a claimed run starts immediately instead of waiting on the flow while holding a worker slot.
//  * A run that exceeds Execution:MaxFlowDurationSeconds is stopped and Failed with a dead letter (not "Cancelled").
//  * On a graceful stop the Engine stops claiming, lets in-flight runs finish for Execution:ShutdownDrainSeconds,
//    then stops what is left and Fails it with a shutdown message (dead-lettered, so it can be resynced). A run
//    claimed but not yet started goes back to the queue. Runs orphaned by a crash are recovered by
//    StaleExecutionRecoveryWorker.
public class FlowExecutionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ExecutionCancellationRegistry _cancellationRegistry;
    private readonly ExecutionOptions _options;
    private readonly ILogger<FlowExecutionWorker> _logger;
    private readonly FlowRunGate _gate;
    private readonly ConcurrentDictionary<Guid, byte> _runningFlows = new();
    // Flows found running on another Engine: skipped by the claim for a moment instead of claimed and handed back
    // in a tight loop.
    private readonly ConcurrentDictionary<Guid, DateTime> _busyElsewhere = new();
    private static readonly TimeSpan BusyElsewhereCooldown = TimeSpan.FromSeconds(2);
    // Cancelled when the shutdown drain window expires: interrupts whatever is still running.
    private readonly CancellationTokenSource _abortRuns = new();
    private volatile bool _stopRequested;

    public FlowExecutionWorker(
        IServiceProvider serviceProvider,
        ExecutionCancellationRegistry cancellationRegistry,
        IOptions<ExecutionOptions> options,
        ILogger<FlowExecutionWorker> logger,
        FlowRunGate gate)
    {
        _serviceProvider = serviceProvider;
        _gate = gate;
        _cancellationRegistry = cancellationRegistry;
        _options = options.Value;
        _logger = logger;
    }

    private int MaxDurationSeconds => _options.MaxFlowDurationSeconds < 1 ? 600 : _options.MaxFlowDurationSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = _options.MaxConcurrency < 1 ? 4 : _options.MaxConcurrency;
        var pollInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds < 1 ? 2 : _options.PollIntervalSeconds);
        var drain = TimeSpan.FromSeconds(Math.Max(0, _options.ShutdownDrainSeconds));
        _logger.LogInformation(
            "FlowExecutionWorker starting with {MaxConcurrency} workers, polling the database every {PollSeconds}s",
            concurrency, pollInterval.TotalSeconds);
        using var stopRegistration = stoppingToken.Register(() => _stopRequested = true);

        // One poller instead of one polling loop per worker. The poller only asks the database for work when a
        // worker slot is free, so an idle Engine runs one claim query per interval (it used to run MaxConcurrency
        // of them), and a burst is still drained immediately: after a successful claim the poller loops straight
        // back for the next row without waiting for the interval. Concurrency is capped by the slot semaphore.
        var slots = new SemaphoreSlim(concurrency, concurrency);
        var running = new ConcurrentDictionary<Task, byte>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);

                QueuedExecutionClaim? claim = null;
                IAsyncDisposable? lease = null;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();
                    claim = await repository.TryClaimNextQueuedExecutionAsync(ExcludedFlows());
                    if (claim != null)
                    {
                        lease = await TryEnterFlowAsync(claim, repository);
                        if (lease == null)
                        {
                            claim = null;
                            slots.Release();
                            continue; // the flow is running elsewhere: look for other work straight away
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    slots.Release();
                    break;
                }
                catch (SqliteException ex)
                {
                    // Write contention with the API process under WAL is transient by design.
                    _logger.LogDebug(ex, "SQLite busy while claiming the next queued execution");
                }
                catch (Npgsql.NpgsqlException ex) when (ex.IsTransient)
                {
                    // A dropped or restarting PostgreSQL connection: the next poll retries.
                    _logger.LogWarning(ex, "Transient PostgreSQL error while claiming the next queued execution");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error claiming the next queued execution");
                }

                if (claim == null)
                {
                    slots.Release();
                    try
                    {
                        await Task.Delay(pollInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                _runningFlows[claim.FlowId] = 0;
                var flowId = claim.FlowId;
                var task = RunClaimAsync(claim, lease!);
                running[task] = 0;
                _ = task.ContinueWith(finished =>
                {
                    running.TryRemove(finished, out _);
                    _runningFlows.TryRemove(flowId, out _);
                    slots.Release();
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        var inFlight = running.Keys.ToArray();
        if (inFlight.Length == 0)
        {
            return;
        }

        _logger.LogInformation("Engine stopping: waiting up to {DrainSeconds}s for {Count} in-flight run(s) to finish",
            drain.TotalSeconds, inFlight.Length);
        var all = Task.WhenAll(inFlight);
        if (await Task.WhenAny(all, Task.Delay(drain)) != all)
        {
            _logger.LogWarning("Engine stopping: {Count} run(s) did not finish within {DrainSeconds}s and are being stopped",
                running.Count, drain.TotalSeconds);
            _abortRuns.Cancel();
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15)));
        }
    }

    private List<Guid> ExcludedFlows()
    {
        var now = DateTime.UtcNow;
        foreach (var (flowId, until) in _busyElsewhere)
        {
            if (until < now) _busyElsewhere.TryRemove(flowId, out _);
        }

        return _runningFlows.Keys.Concat(_busyElsewhere.Keys).Distinct().ToList();
    }

    // Takes the flow's run gate for a claimed run. If another Engine is running the flow, the claim goes back to the
    // queue untouched and the flow is skipped briefly.
    private async Task<IAsyncDisposable?> TryEnterFlowAsync(QueuedExecutionClaim claim, IExecutionRepository repository)
    {
        IAsyncDisposable? lease;
        try
        {
            lease = await _gate.TryEnterAsync(claim.FlowId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not take the run gate for flow {FlowId}; returning execution {ExecutionId} to the queue", claim.FlowId, claim.ExecutionId);
            lease = null;
        }

        if (lease != null)
        {
            return lease;
        }

        _busyElsewhere[claim.FlowId] = DateTime.UtcNow + BusyElsewhereCooldown;
        try
        {
            await repository.ReleaseClaimAsync(claim.ExecutionId);
            _logger.LogDebug("Flow {FlowId} is running on another Engine; execution {ExecutionId} returned to the queue", claim.FlowId, claim.ExecutionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not return execution {ExecutionId} to the queue; stale-run recovery will resolve it", claim.ExecutionId);
        }

        return null;
    }

    // Runs one claimed execution to completion. Never throws: every failure is logged here so a bad run cannot
    // take the poller down. Holds the flow's run gate (lease) until the run is finished.
    private async Task RunClaimAsync(QueuedExecutionClaim claim, IAsyncDisposable lease)
    {
        await using var gate = lease;
        await Task.Yield();

        // Everything logged inside this scope carries the correlation ids, so the
        // database log sink can populate its ExecutionId/FlowId columns and the Debug
        // Logs console can filter a whole run by execution.
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
            ["IsTest"] = claim.IsTest,
            ["TestCaseId"] = claim.TestCaseId,
            ["TestRunId"] = claim.TestRunId
        });

        var claimedAt = Stopwatch.GetTimestamp();
        _logger.LogInformation(new EventId(1200, "flow.execution.claimed"),
            "Claimed queued execution {ExecutionId} for flow {FlowId} (trigger {TriggerSource}, queue wait {QueueWaitMs}ms)",
            claim.ExecutionId, claim.FlowId, string.IsNullOrWhiteSpace(claim.TriggerSource) ? "Manual" : claim.TriggerSource, claim.QueuedAt.HasValue ? (long)Math.Max(0, (DateTime.UtcNow - claim.QueuedAt.Value).TotalMilliseconds) : 0);

        try
        {
            await ExecuteRequestAsync(claim);
            _logger.LogInformation(
                "Released execution {ExecutionId} for flow {FlowId} after {DurationMs}ms",
                claim.ExecutionId, claim.FlowId, (long)Stopwatch.GetElapsedTime(claimedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Execution {ExecutionId} abandoned after {DurationMs}ms because the Engine is shutting down",
                claim.ExecutionId, (long)Stopwatch.GetElapsedTime(claimedAt).TotalMilliseconds);
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

    // Runs of one flow are serialised across Engines (cross references and persisted state assume it): the caller
    // holds the flow's run gate for the whole run.
    private async Task ExecuteRequestAsync(QueuedExecutionClaim request)
    {
        {
            using var scope = _serviceProvider.CreateScope();
            var executionRepository = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();

            // Stopping before the run started: hand it back to the queue for the next Engine instead of starting it.
            if (_stopRequested)
            {
                if (await executionRepository.ReleaseClaimAsync(request.ExecutionId))
                {
                    _logger.LogInformation("Execution {ExecutionId} returned to the queue because the Engine is stopping", request.ExecutionId);
                }

                return;
            }

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

            // Two ways a run is interrupted, both failures (not user cancels): the maximum run time, and the end of
            // the shutdown drain window. A user cancel arrives through the registry's token.
            var maxDuration = MaxDurationSeconds;
            using var interruption = CancellationTokenSource.CreateLinkedTokenSource(_abortRuns.Token);
            interruption.CancelAfter(TimeSpan.FromSeconds(maxDuration));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, interruption.Token);

            FlowTestContext? testContext = null;
            FlowTestRun? testRun = null;
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
            }

            var executor = scope.ServiceProvider.GetRequiredService<FlowExecutor>();
            executor.UseInterruption(interruption.Token, () => _abortRuns.IsCancellationRequested
                ? $"The Engine shut down before this run finished; it was stopped after the {Math.Max(0, _options.ShutdownDrainSeconds)} s shutdown grace period."
                : $"The run exceeded the maximum run time of {maxDuration} s (Execution:MaxFlowDurationSeconds) and was stopped.");
            var startedAt = Stopwatch.GetTimestamp();
            FlowExecution result;
            if (!string.IsNullOrWhiteSpace(execution.StartStepIdsJson))
            {
                var startStepIds = JsonSerializer.Deserialize<Guid[]>(execution.StartStepIdsJson) ?? Array.Empty<Guid>();
                result = await executor.ExecuteFlowFromNodesAsync(
                    request.FlowId,
                    request.ExecutionId,
                    startStepIds,
                    execution.InitialFlowStateJson ?? "{}",
                    execution.InitialPersistedStateJson ?? "{}",
                    linked.Token,
                    testContext);
            }
            else
            {
                result = await executor.ExecuteFlowAsync(request.FlowId, request.ExecutionId, request.TriggerPayload, linked.Token, testContext);
            }
            var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (testRun != null && testContext != null)
            {
                var integrations = scope.ServiceProvider.GetRequiredService<IIntegrationRepository>();
                var flow = await integrations.GetByIdAsync(request.FlowId);
                if (flow != null)
                {
                    var recorder = scope.ServiceProvider.GetRequiredService<FlowTestResultRecorder>();
                    await recorder.RecordAsync(
                        testRun.Id, testRun.TestCaseId, result, flow, testContext.Definition, CancellationToken.None);
                }

                _logger.LogInformation("Test case {TestCaseId} {TestStatus} for execution {ExecutionId}",
                    testRun.TestCaseId, testRun.Status, execution.Id);
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
    }

    public override void Dispose()
    {
        _abortRuns.Dispose();
        base.Dispose();
    }
}
