using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using SimpleIPaaS.Infrastructure.Security;

namespace SimpleIPaaS.Api.Security;

public class AuthContextMiddleware
{
    private readonly RequestDelegate _next;

    public AuthContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, UserManager<AppUser> userManager)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userIdValue = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;
            var user = Guid.TryParse(userIdValue, out var userId)
                ? await userManager.FindByIdAsync(userId.ToString())
                : null;

            if (user == null || user.IsDisabled || await userManager.IsLockedOutAsync(user))
            {
                await RejectAsync(context);
                return;
            }

            var securityStamp = context.User.FindFirst("security_stamp")?.Value;
            if (!string.IsNullOrWhiteSpace(securityStamp) &&
                !string.Equals(securityStamp, user.SecurityStamp, StringComparison.Ordinal))
            {
                await RejectAsync(context);
                return;
            }
        }

        await _next(context);
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            title = "Unauthorized",
            detail = "This session is no longer valid. Sign in again.",
            status = StatusCodes.Status401Unauthorized
        });
    }
}
