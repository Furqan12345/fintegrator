using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;

namespace SimpleIPaaS.Engine.Workers;

// Recovers runs orphaned by an Engine that died (crash, kill -9, power loss, OOM). Such a run stayed InProgress
// forever: no failure, no alert, a permanently "running" row in the UI.
//
// No run can legitimately be InProgress longer than Execution:MaxFlowDurationSeconds (the Engine stops it), so a run
// older than that plus Execution:StaleRunGraceSeconds belongs to a dead process. Test runs may run inside the API,
// capped at 120 s, so their cutoff is max(MaxFlowDurationSeconds, 120) plus the grace. Each row is failed with a guarded update, so any number
// of Engines can sweep at once. The run is not retried automatically: its cards may already have had side effects.
// It gets a dead letter (RecoveredRunDeadLetters) so it alerts and can be replayed by hand.
public sealed class StaleExecutionRecoveryWorker : BackgroundService
{
    private const int TestRunCapSeconds = 120;
    private readonly IServiceScopeFactory _scopes;
    private readonly ExecutionOptions _options;
    private readonly ILogger<StaleExecutionRecoveryWorker> _logger;

    public StaleExecutionRecoveryWorker(IServiceScopeFactory scopes, IOptions<ExecutionOptions> options, ILogger<StaleExecutionRecoveryWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    private int MaxDuration => _options.MaxFlowDurationSeconds < 1 ? 600 : _options.MaxFlowDurationSeconds;

    internal TimeSpan StaleAfter => TimeSpan.FromSeconds(MaxDuration + Math.Max(0, _options.StaleRunGraceSeconds));

    internal TimeSpan TestRunStaleAfter => TimeSpan.FromSeconds(Math.Max(MaxDuration, TestRunCapSeconds) + Math.Max(0, _options.StaleRunGraceSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sweep often enough that recovery happens within a fraction of the threshold, but at most once a minute.
        var interval = TimeSpan.FromSeconds(Math.Clamp(StaleAfter.TotalSeconds / 4, 5, 60));
        _logger.LogInformation("Stale-run recovery: runs InProgress for more than {StaleSeconds}s are failed (sweep every {IntervalSeconds}s)",
            StaleAfter.TotalSeconds, interval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Stale-run recovery sweep failed; retrying next interval");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<int> SweepAsync()
    {
        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();
        var message = $"The Engine running this transaction stopped unexpectedly (no result after {(int)StaleAfter.TotalSeconds} s). " +
                      "It was marked failed by recovery and was not retried automatically, because some of its cards may already have run.";
        var now = DateTime.UtcNow;
        var failed = await repository.FailStaleExecutionsAsync(now - StaleAfter, now - TestRunStaleAfter, message);
        var deadLetters = scope.ServiceProvider.GetRequiredService<RecoveredRunDeadLetters>();
        foreach (var id in failed)
        {
            _logger.LogError(new EventId(1250, "flow.execution.recovered"),
                "Execution {ExecutionId} was InProgress with no live Engine for over {StaleSeconds}s; marked Failed by recovery", id, StaleAfter.TotalSeconds);

            // A dead letter makes the run alert and gives it a replay entry point. Only the sweep that failed the
            // run gets its id (the update is guarded per row), so several Engines never write two.
            try
            {
                var entry = await deadLetters.CreateAsync(id, message);
                if (entry != null)
                {
                    _logger.LogInformation("Recovered run {ExecutionId}: dead letter {DeadLetterId} written for card {NodeName} (manual replay only)",
                        id, entry.Id, entry.NodeName);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Recovered run {ExecutionId}: the dead letter could not be written; the run stays Failed", id);
            }
        }

        return failed.Count;
    }
}
