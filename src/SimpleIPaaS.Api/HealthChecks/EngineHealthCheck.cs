using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Api.HealthChecks;

public sealed class EngineHealthCheck : IHealthCheck
{
    private readonly IPaaSContext _context;
    private readonly OperationsOptions _options;

    public EngineHealthCheck(IPaaSContext context, IOptions<OperationsOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var timeoutSeconds = _options.EngineHeartbeatTimeoutSeconds < 30
            ? 90
            : _options.EngineHeartbeatTimeoutSeconds;
        var cutoff = DateTime.UtcNow.AddSeconds(-timeoutSeconds);

        var isAlive = await _context.HostHeartbeats.AnyAsync(
            heartbeat => heartbeat.ServiceName == "Engine"
                && heartbeat.Status == "Healthy"
                && heartbeat.LastSeenAt >= cutoff,
            cancellationToken);

        return isAlive
            ? HealthCheckResult.Healthy("The execution engine heartbeat is current.")
            : HealthCheckResult.Unhealthy($"No healthy execution engine heartbeat was received in the last {timeoutSeconds} seconds.");
    }
}