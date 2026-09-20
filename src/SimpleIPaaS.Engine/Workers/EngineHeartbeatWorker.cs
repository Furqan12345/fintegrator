using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Engine.Workers;

public sealed class EngineHeartbeatWorker : BackgroundService
{
    private const string ServiceName = "Engine";
    private readonly IServiceProvider _serviceProvider;
    private readonly OperationsOptions _options;
    private readonly ILogger<EngineHeartbeatWorker> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public EngineHeartbeatWorker(
        IServiceProvider serviceProvider,
        IOptions<OperationsOptions> options,
        ILogger<EngineHeartbeatWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.EngineHeartbeatIntervalSeconds < 5
            ? 15
            : _options.EngineHeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WriteHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Engine heartbeat update failed.");
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

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
        var heartbeat = await context.HostHeartbeats
            .FirstOrDefaultAsync(item => item.ServiceName == ServiceName && item.InstanceId == _instanceId, cancellationToken);

        if (heartbeat == null)
        {
            context.HostHeartbeats.Add(new HostHeartbeat
            {
                ServiceName = ServiceName,
                InstanceId = _instanceId,
                LastSeenAt = DateTime.UtcNow
            });
        }
        else
        {
            heartbeat.LastSeenAt = DateTime.UtcNow;
            heartbeat.Status = "Healthy";
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}