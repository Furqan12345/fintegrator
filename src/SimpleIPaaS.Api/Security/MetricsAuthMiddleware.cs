using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace SimpleIPaaS.Api.Security;

// Prometheus cannot complete the interactive login flow. Keep /metrics behind a separate
// long-lived deployment key instead of exposing it anonymously.
public class MetricsAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _expectedKey;

    public MetricsAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _expectedKey = configuration["Metrics:ApiKey"] ?? string.Empty;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var provided = context.Request.Headers["X-Metrics-Key"].FirstOrDefault()
            ?? context.Request.Headers["X-Api-Key"].FirstOrDefault()
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_expectedKey) || !FixedTimeEquals(_expectedKey, provided))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Unauthorized",
                detail = "A valid metrics key is required.",
                status = StatusCodes.Status401Unauthorized
            });
            return;
        }

        var identity = new ClaimsIdentity("MetricsKey");
        identity.AddClaim(new Claim(ClaimTypes.Name, "metrics"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        context.User = new ClaimsPrincipal(identity);
        await _next(context);
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(provided);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
