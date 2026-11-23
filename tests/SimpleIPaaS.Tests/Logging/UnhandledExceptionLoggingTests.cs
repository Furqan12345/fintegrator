using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Api.Observability;
using SimpleIPaaS.Infrastructure.Logging;

namespace SimpleIPaaS.Tests.Logging;

// Regression guard for the finding that an unhandled 5xx left no trace in the log store: the
// exception handler in Program.cs logs through UnhandledExceptionLogger under a category the
// sink cannot filter out, and the message text carries the HTTP method, the path and the trace
// id so /api/logs?search=<traceId> (which matches AppLogEntries.Message) finds it.
public sealed class UnhandledExceptionLoggingTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object> Scope)> Entries { get; } = new();

        private readonly List<Dictionary<string, object>> _scopes = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var values = new Dictionary<string, object>();
            if (state is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var pair in pairs) values[pair.Key] = pair.Value;
            }
            _scopes.Add(values);
            return new Scope(() => _scopes.Remove(values));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object>();
            foreach (var item in _scopes)
            {
                foreach (var pair in item) scope[pair.Key] = pair.Value;
            }

            Entries.Add((logLevel, formatter(state, exception), exception, scope));
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _dispose;
            public Scope(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }

    [Fact]
    public void UnhandledRequestException_IsLoggedAtErrorWithMethodPathAndTraceIdInTheMessage()
    {
        var logger = new CapturingLogger();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/integrationflow/boom/rollback/1";
        context.TraceIdentifier = "trace-abc-123";

        UnhandledExceptionLogger.Log(logger, context, new InvalidOperationException("kaboom"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Contains("POST", entry.Message);
        Assert.Contains("/api/integrationflow/boom/rollback/1", entry.Message);
        Assert.Contains("trace-abc-123", entry.Message);
        // The trace id is also a structured value, so the trace_id column is populated too.
        Assert.Equal("trace-abc-123", entry.Scope["TraceId"]);
    }

    [Fact]
    public void UnhandledExceptionCategory_IsNeverFilteredOutByTheDatabaseSink()
    {
        // The sink drops noisy framework categories (EF Core, Sqlite, HttpClient, its own
        // namespace). The unhandled exception category must not match any of them, otherwise a 5xx
        // would never reach the store the /api/logs search reads.
        Assert.DoesNotContain(
            DatabaseLoggerProvider.AlwaysExcludedCategoryPrefixes,
            prefix => UnhandledExceptionLogger.CategoryName.StartsWith(prefix, StringComparison.Ordinal));
    }
}
