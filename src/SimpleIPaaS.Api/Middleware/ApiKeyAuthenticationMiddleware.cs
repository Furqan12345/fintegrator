using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Infrastructure.MultiTenancy;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Api.Middleware;

public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;

    public ApiKeyAuthenticationMiddleware(RequestDelegate next, IHostEnvironment environment)
    {
        _next = next;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context, IPaaSContext db, ITenantContext tenantContext)
    {
        if (IsExempt(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader) || string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            await WriteUnauthorizedAsync(context, "Missing X-Api-Key header.");
            return;
        }

        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKeyHeader.ToString())));
        var apiKey = await db.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.RevokedAt == null, context.RequestAborted);

        if (apiKey == null)
        {
            await WriteUnauthorizedAsync(context, "Invalid or revoked API key.");
            return;
        }

        tenantContext.SetTenantId(apiKey.TenantId);
        await _next(context);
    }

    private bool IsExempt(PathString path)
    {
        if (path.StartsWithSegments("/health")) return true;
        if (path.StartsWithSegments("/api/webhooks")) return true;
        if (_environment.IsDevelopment() && path.StartsWithSegments("/swagger")) return true;
        return false;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = detail
        });
    }
}
