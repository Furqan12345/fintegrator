using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Api.Observability;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("metrics")]
public sealed class MetricsController : ControllerBase
{
    private readonly IPaaSContext _context;
    private readonly OperationalMetrics _metrics;
    private readonly OperationsOptions _options;

    public MetricsController(
        IPaaSContext context,
        OperationalMetrics metrics,
        IOptions<OperationsOptions> options)
    {
        _context = context;
        _metrics = metrics;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
    {
        var counts = await _context.FlowExecutions
            .AsNoTracking()
            .GroupBy(execution => execution.Status)
            .Select(group => new { Status = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);

        var heartbeat = await _context.HostHeartbeats
            .AsNoTracking()
            .Where(item => item.ServiceName == "Engine" && item.Status == "Healthy")
            .OrderByDescending(item => item.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken);

        var heartbeatAge = heartbeat == null
            ? -1
            : Math.Max(0, (DateTime.UtcNow - heartbeat.LastSeenAt).TotalSeconds);
        var timeoutSeconds = _options.EngineHeartbeatTimeoutSeconds < 30 ? 90 : _options.EngineHeartbeatTimeoutSeconds;
        var heartbeatHealthy = heartbeat != null && heartbeatAge <= timeoutSeconds;

        return Content(
            _metrics.RenderPrometheus(counts, heartbeatAge, heartbeatHealthy),
            "text/plain; version=0.0.4; charset=utf-8");
    }
}