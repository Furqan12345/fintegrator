using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace SimpleIPaaS.Infrastructure.Logging;

// ILoggerProvider that persists application logs into the shared SQLite database.
//
// Standard "Logging:LogLevel:*" filtering still applies: the logging framework evaluates
// its rules per provider (alias "Database") before ever calling into this type, and the
// provider then applies its own "Logging:Database:MinimumLevel" floor on top.
[ProviderAlias("Database")]
public sealed class DatabaseLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    // RECURSION GUARD — non-negotiable, deliberately not configurable.
    //
    // Persisting a log entry executes SQL. EF Core logs every command it executes through
    // ILogger under "Microsoft.EntityFrameworkCore.Database.Command", and the LogsController
    // reads this very table through EF. Without this filter, one read would log a command,
    // which would insert a row, which the UI would poll, which would log another command…
    // an unbounded feedback loop. Microsoft.Data.Sqlite and this namespace are excluded for
    // the same reason.
    public static readonly string[] AlwaysExcludedCategoryPrefixes =
    {
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data.Sqlite",
        "SimpleIPaaS.Infrastructure.Logging"
    };

    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private readonly string[] _excludedPrefixes;
    private bool _disposed;

    public DatabaseLoggerProvider(DatabaseLoggerOptions options, string source, string connectionString)
        : this(options, source, new DatabaseLogWriter(options, connectionString))
    {
    }

    // Test seam: inject a writer directly.
    public DatabaseLoggerProvider(DatabaseLoggerOptions options, string source, DatabaseLogWriter writer)
    {
        Options = options;
        Source = source;
        Writer = writer;
        _excludedPrefixes = AlwaysExcludedCategoryPrefixes
            .Concat(options.ExcludedCategoryPrefixes ?? Array.Empty<string>())
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .ToArray();
    }

    internal DatabaseLoggerOptions Options { get; }

    internal string Source { get; }

    internal DatabaseLogWriter Writer { get; }

    internal IExternalScopeProvider? ScopeProvider { get; private set; }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => ScopeProvider = scopeProvider;

    public ILogger CreateLogger(string categoryName)
    {
        var category = categoryName ?? string.Empty;
        return _loggers.GetOrAdd(category, name => IsExcluded(name)
            ? DisabledLogger.Instance
            : new DatabaseLogger(this, name));
    }

    public bool IsExcluded(string categoryName)
    {
        foreach (var prefix in _excludedPrefixes)
        {
            if (categoryName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loggers.Clear();
        Writer.Dispose();
    }

    // A logger for an excluded category: enabled for nothing, so the framework short-circuits
    // before formatting anything.
    private sealed class DisabledLogger : ILogger
    {
        public static readonly DisabledLogger Instance = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
