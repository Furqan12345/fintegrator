using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SimpleIPaaS.Infrastructure.Security;

namespace SimpleIPaaS.Api.Security;

public class ViewerReadOnlyFilter : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true || !user.IsInRole(AppRoles.Viewer))
        {
            return Task.CompletedTask;
        }

        var path = context.HttpContext.Request.Path;
        if (path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/admin"))
        {
            return Task.CompletedTask;
        }

        var method = context.HttpContext.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            context.Result = new ForbidResult();
        }

        return Task.CompletedTask;
    }
}
