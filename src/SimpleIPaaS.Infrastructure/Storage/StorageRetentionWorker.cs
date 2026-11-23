using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleIPaaS.Infrastructure.Storage;

// Applies retention on a schedule. Registered in both hosts so retention keeps running whichever
// process happens to be alive; every operation is idempotent, and a dataset is only swept by the
// backend it is actually configured for.
public sealed class StorageRetentionWorker : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StorageRetentionWorker> _logger;
    private readonly TimeSpan _interval;

    public StorageRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<StorageRetentionWorker> logger,
        TimeSpan? interval = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start staggered so the Api and the Engine do not sweep at the same instant.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20 + Random.Shared.Next(20)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var retention = scope.ServiceProvider.GetRequiredService<StorageRetentionService>();
                var result = await retention.RunAsync(stoppingToken).ConfigureAwait(false);

                foreach (var outcome in result.Datasets)
                {
                    if (outcome.Removed > 0 || !string.IsNullOrEmpty(outcome.Error))
                    {
                        _logger.LogInformation(
                            "Storage retention {Dataset} ({Backend}): {Action}; removed={Removed} {Error}",
                            outcome.Dataset, outcome.Backend, outcome.Action, outcome.Removed, outcome.Error ?? string.Empty);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Storage retention pass failed; it will run again on the next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
