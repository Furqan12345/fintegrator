using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SimpleIPaaS.Api.Observability;

namespace SimpleIPaaS.Api.Middleware;

public sealed class OperationalMetricsMiddleware
{
    private readonly RequestDelegate _next;

    public OperationalMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, OperationalMetrics metrics)
    {
        var startedAt = Stopwatch.GetTimestamp();
        metrics.RequestStarted();

        try
        {
            await _next(context);
        }
        finally
        {
            metrics.RequestFinished(
                context.Response.StatusCode,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }
}