using System;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Logging;

internal sealed class DatabaseLogger : ILogger
{
    private readonly DatabaseLoggerProvider _provider;
    private readonly string _category;

    public DatabaseLogger(DatabaseLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        _provider.ScopeProvider?.Push(state) ?? NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) =>
        _provider.Options.Enabled && logLevel != LogLevel.None && logLevel >= _provider.Options.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        // A logging sink must never throw back into the caller.
        try
        {
            // Scopes first (outermost to innermost), then the message state, so the most
            // specific value for a property wins.
            var correlation = new CorrelationBox();
            _provider.ScopeProvider?.ForEachScope(static (scope, box) => box.Harvest(scope), correlation);
            correlation.Harvest(state);

            var entry = new AppLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = logLevel.ToString(),
                Source = _provider.Source,
                Category = _category,
                Message = Truncate(formatter(state, exception), _provider.Options.MaxMessageLength),
                Exception = exception == null
                    ? null
                    : Truncate(exception.ToString(), _provider.Options.MaxExceptionLength),
                TenantId = correlation.Value.TenantId,
                FlowExecutionId = correlation.Value.FlowExecutionId,
                FlowId = correlation.Value.FlowId,
                NodeName = correlation.Value.NodeName
            };

            _provider.Writer.TryEnqueue(entry);
        }
        catch (Exception)
        {
            // Swallow: losing a log line is always preferable to breaking the caller.
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength) + "… [truncated]";
    }

    // Mutable holder so ForEachScope's static (non-capturing) callback can accumulate into
    // the same LogCorrelation across every scope level.
    private sealed class CorrelationBox
    {
        public LogCorrelation Value;

        public void Harvest(object? state) => Value.Harvest(state);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
