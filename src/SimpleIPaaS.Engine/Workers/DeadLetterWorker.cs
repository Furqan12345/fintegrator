using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Engine.Workers;

// Replays dead letters. Auto-retry candidates (Pending) follow the configured
// MaxAutoRetryAttempts limit; entries the API flagged via ReplayRequestedAt are
// manual replays that bypass that limit — the marker is cleared before executing
// so a crashed replay can simply be re-requested from the UI.
public class DeadLetterWorker : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DeadLetterWorker> _logger;

    public DeadLetterWorker(IServiceProvider serviceProvider, ILogger<DeadLetterWorker> logger)
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
                await ProcessReplayableAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing dead letters");
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

    private async Task ProcessReplayableAsync(CancellationToken stoppingToken)
    {
        List<(DeadLetterEntry Entry, bool Force)> work;

        using (var scanScope = _serviceProvider.CreateScope())
        {
            var executionRepository = scanScope.ServiceProvider.GetRequiredService<IExecutionRepository>();
            var deadLetterService = scanScope.ServiceProvider.GetRequiredService<DeadLetterService>();
            var candidates = await executionRepository.GetReplayableDeadLettersAcrossTenantsAsync(100);

            work = new List<(DeadLetterEntry, bool)>();

            foreach (var candidate in candidates)
            {
                if (candidate.ReplayRequestedAt != null)
                {
                    // Manual replay request made through the API: clear the marker first,
                    // then execute with force semantics (bypasses auto-retry limits).
                    var tracked = await executionRepository.GetDeadLetterAsync(candidate.Id);
                    if (tracked == null || tracked.ReplayRequestedAt == null)
                    {
                        continue; // another engine instance already claimed it
                    }

                    tracked.ReplayRequestedAt = null;
                    await executionRepository.UpdateDeadLetterEntryAsync(tracked);
                    work.Add((tracked, true));
                }
                else if (deadLetterService.CanAutoRetry(candidate))
                {
                    work.Add((candidate, false));
                }
            }
        }

        if (work.Count > 0)
        {
            _logger.LogInformation(
                "Dead letter scan found {ReplayCount} entr(y/ies) to replay ({ManualCount} manually requested)",
                work.Count, work.Count(item => item.Force));
        }

        foreach (var (entry, force) in work)
        {
            stoppingToken.ThrowIfCancellationRequested();

            using var correlationScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["FlowExecutionId"] = entry.FlowExecutionId,
                ["TenantId"] = entry.TenantId,
                ["NodeName"] = entry.NodeName ?? string.Empty
            });

            var startedAt = Stopwatch.GetTimestamp();
            _logger.LogInformation(
                "Replaying dead letter {DeadLetterId} for execution {FlowExecutionId} node '{NodeName}' (forced: {Forced}, attempt {RetryCount})",
                entry.Id, entry.FlowExecutionId, entry.NodeName, force, entry.RetryCount);

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                tenantContext.SetTenantId(entry.TenantId);

                var deadLetterService = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
                await deadLetterService.ReplayEntryAsync(entry.Id, force, stoppingToken);

                _logger.LogInformation(
                    "Dead letter {DeadLetterId} replay finished in {DurationMs}ms",
                    entry.Id, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error replaying dead letter {DeadLetterId}", entry.Id);
            }
        }
    }
}
