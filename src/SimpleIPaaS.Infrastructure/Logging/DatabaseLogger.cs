using System;
using System.Diagnostics;
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

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _provider.ScopeProvider?.Push(state) ?? NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => _provider.Options.Enabled && logLevel != LogLevel.None && logLevel >= _provider.Options.MinimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        try
        {
            var correlation = new LogCorrelation();
            _provider.ScopeProvider?.ForEachScope(static (scope, box) => box.Harvest(scope), correlation);
            correlation.HarvestActivity(Activity.Current);
            correlation.Harvest(state);
            var outcome = correlation.Outcome ?? (logLevel >= LogLevel.Error ? "Failed" : logLevel >= LogLevel.Warning ? "Warning" : "Succeeded");
            var entry = new AppLogEntry
            {
                EventId = Guid.NewGuid(),
                EventVersion = 1,
                Timestamp = DateTime.UtcNow,
                Level = logLevel.ToString(),
                Source = _provider.Source,
                Service = _provider.Source,
                HostInstance = _provider.InstanceId,
                EnvironmentName = _provider.Options.EnvironmentName,

                Category = _category,
                Component = correlation.Component ?? _category,
                EventName = correlation.EventName ?? eventId.Name ?? "log",
                Operation = correlation.Operation ?? string.Empty,
                Outcome = outcome,
                DurationMs = correlation.DurationMs,
                TraceId = correlation.TraceId,
                SpanId = correlation.SpanId,
                ParentSpanId = correlation.ParentSpanId,
                ApplicationVersion = correlation.ApplicationVersion ?? _provider.Options.ApplicationVersion,
                Message = Truncate(ObservabilitySanitizer.Text(formatter(state, exception)), _provider.Options.MaxMessageLength),
                Exception = exception == null ? null : Truncate(ObservabilitySanitizer.Text(exception.ToString()), _provider.Options.MaxExceptionLength),
                TenantId = correlation.TenantId,
                FlowExecutionId = correlation.FlowExecutionId,
                FlowId = correlation.FlowId,
                FlowName = correlation.FlowName,
                IntegrationId = correlation.IntegrationId,
                IntegrationName = correlation.IntegrationName,
                ConnectionId = correlation.ConnectionId,
                CardId = correlation.CardId,
                CardType = correlation.CardType,
                StepExecutionId = correlation.StepExecutionId,
                Invocation = correlation.Invocation,
                LoopPath = correlation.LoopPath,
                RetryAttempt = correlation.RetryAttempt,
                IsTest = correlation.IsTest,
                TestCaseId = correlation.TestCaseId,
                TestRunId = correlation.TestRunId,
                NodeName = correlation.NodeName,
                PropertiesJson = correlation.ToPropertiesJson()
            };
            _provider.Writer.TryEnqueue(entry);
        }
        catch (Exception)
        {
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0 || value.Length <= maxLength) return value;
        return value[..maxLength] + "… [truncated]";
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}