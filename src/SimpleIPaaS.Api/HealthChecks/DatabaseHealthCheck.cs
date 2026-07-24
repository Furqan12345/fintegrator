using Microsoft.Extensions.Diagnostics.HealthChecks;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Api.HealthChecks;

public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IPaaSContext _context;

    public DatabaseHealthCheck(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var canConnect = await _context.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? HealthCheckResult.Healthy("Database is reachable.")
            : HealthCheckResult.Unhealthy("Database is unreachable.");
    }
}
