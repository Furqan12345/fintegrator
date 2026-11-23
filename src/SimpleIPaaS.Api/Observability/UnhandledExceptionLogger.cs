using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SimpleIPaaS.Api.Observability;

// Logging for unhandled request exceptions.
//
// The trace identifier is written into the message text as well as into the structured
// fields, because the Debug Logs console (and the /api/logs search) matches on message
// text: an operator quoting the traceId from a 500 body has to find the failure with a
// plain text search. Extracted from the exception handler in Program.cs so the behaviour
// is unit-testable.
public static class UnhandledExceptionLogger
{
    // Deliberately outside the "Microsoft.*" category space so the host's
    // "Logging:LogLevel:Microsoft.AspNetCore = Warning" rule cannot filter the error out,
    // and outside the log sink's recursion guard so it is always persisted.
    public const string CategoryName = "SimpleIPaaS.Api.UnhandledException";

    public const string MessageTemplate =
        "Unhandled exception processing {Method} {Path} [traceId: {TraceId}]";

    public static string ResolveTraceId(HttpContext context) =>
        Activity.Current?.Id ?? context.TraceIdentifier;

    public static void Log(ILogger logger, HttpContext context, Exception exception)
    {
        var traceId = ResolveTraceId(context);
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // Structured properties + message text so both the trace id column and a
        // free-text search can find the failure.
        using (logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = traceId }))
        {
            logger.LogError(exception, MessageTemplate, method, path, traceId);
        }
    }
}
