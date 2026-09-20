using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleIPaaS.Infrastructure.Logging;

internal sealed class LogCorrelation
{
    public Guid? TenantId;
    public Guid? FlowExecutionId;
    public Guid? FlowId;
    public string? FlowName;
    public Guid? IntegrationId;
    public string? IntegrationName;
    public Guid? ConnectionId;
    public Guid? CardId;
    public string? CardType;
    public Guid? StepExecutionId;
    public int? Invocation;
    public string? LoopPath;
    public int? RetryAttempt;
    public bool? IsTest;
    public Guid? TestCaseId;
    public Guid? TestRunId;
    public string? NodeName;
    public string? EventName;
    public string? Component;
    public string? Operation;
    public string? Outcome;
    public long? DurationMs;
    public string? TraceId;
    public string? SpanId;
    public string? ParentSpanId;
    public string? ApplicationVersion;
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Harvest(object? state)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> properties)
        {
            foreach (var property in properties)
            {
                Apply(property.Key, property.Value);
            }
        }
    }

    public void HarvestActivity(Activity? activity)
    {
        if (activity == null) return;
        TraceId ??= activity.TraceId.ToString();
        SpanId ??= activity.SpanId.ToString();
        ParentSpanId ??= activity.ParentSpanId.ToString();
    }

    public string ToPropertiesJson()
    {
        try
        {
            return JsonSerializer.Serialize(Properties);
        }
        catch
        {
            return "{}";
        }
    }

    private void Apply(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Equals("{OriginalFormat}", StringComparison.OrdinalIgnoreCase)) return;
        Properties[key] = ObservabilitySanitizer.Value(value);
        if (value is null) return;

        if (Matches(key, "TenantId")) TenantId = AsGuid(value) ?? TenantId;
        else if (Matches(key, "ExecutionId", "FlowExecutionId")) FlowExecutionId = AsGuid(value) ?? FlowExecutionId;
        else if (Matches(key, "FlowId")) FlowId = AsGuid(value) ?? FlowId;
        else if (Matches(key, "FlowName")) FlowName = AsText(value) ?? FlowName;
        else if (Matches(key, "IntegrationId")) IntegrationId = AsGuid(value) ?? IntegrationId;
        else if (Matches(key, "IntegrationName")) IntegrationName = AsText(value) ?? IntegrationName;
        else if (Matches(key, "ConnectionId")) ConnectionId = AsGuid(value) ?? ConnectionId;
        else if (Matches(key, "CardId", "NodeId", "StepId")) CardId = AsGuid(value) ?? CardId;
        else if (Matches(key, "CardType", "StepType")) CardType = AsText(value) ?? CardType;
        else if (Matches(key, "StepExecutionId")) StepExecutionId = AsGuid(value) ?? StepExecutionId;
        else if (Matches(key, "NodeName", "CardName")) NodeName = AsText(value) ?? NodeName;
        else if (Matches(key, "Invocation", "InvocationNumber")) Invocation = AsInt(value) ?? Invocation;
        else if (Matches(key, "LoopPath", "LoopContext")) LoopPath = AsText(value) ?? LoopPath;
        else if (Matches(key, "RetryAttempt", "Attempt")) RetryAttempt = AsInt(value) ?? RetryAttempt;
        else if (Matches(key, "IsTest")) IsTest = AsBool(value) ?? IsTest;
        else if (Matches(key, "TestCaseId")) TestCaseId = AsGuid(value) ?? TestCaseId;
        else if (Matches(key, "TestRunId")) TestRunId = AsGuid(value) ?? TestRunId;
        else if (Matches(key, "EventName")) EventName = AsText(value) ?? EventName;
        else if (Matches(key, "Component")) Component = AsText(value) ?? Component;
        else if (Matches(key, "Operation")) Operation = AsText(value) ?? Operation;
        else if (Matches(key, "Outcome", "Status")) Outcome = AsText(value) ?? Outcome;
        else if (Matches(key, "DurationMs", "ElapsedMs", "QueueWaitMs", "LockWaitMs")) DurationMs = AsLong(value) ?? DurationMs;
        else if (Matches(key, "TraceId")) TraceId = AsText(value) ?? TraceId;
        else if (Matches(key, "SpanId")) SpanId = AsText(value) ?? SpanId;
        else if (Matches(key, "ParentSpanId")) ParentSpanId = AsText(value) ?? ParentSpanId;
        else if (Matches(key, "ApplicationVersion")) ApplicationVersion = AsText(value) ?? ApplicationVersion;
    }

    private static bool Matches(string key, params string[] names) => Array.Exists(names, name => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
    private static string? AsText(object value) => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
    private static Guid? AsGuid(object value) => value is Guid guid && guid != Guid.Empty ? guid : Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) && parsed != Guid.Empty ? parsed : null;
    private static int? AsInt(object value) => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static long? AsLong(object value) => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static bool? AsBool(object value) => bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) ? parsed : null;
}

internal static class ObservabilitySanitizer
{
    private static readonly Regex SecretAssignment = new("(?i)(password|passwd|secret|token|api[_-]?key|client[_-]?secret|access[_-]?key)=([^&\\s,;]+)", RegexOptions.Compiled);
    private static readonly Regex SecretJson = new("(?i)(\\\"(?:password|passwd|secret|token|api[_-]?key|client[_-]?secret|access[_-]?key)\\\"\\s*:\\s*)\\\"[^\\\"]*\\\"", RegexOptions.Compiled);
    private static readonly Regex SensitiveHeader = new("(?im)^(authorization|proxy-authorization|cookie|set-cookie):.*$", RegexOptions.Compiled);

    public static object? Value(object? value)
    {
        if (value is string text) return Text(text);
        if (value is Array array && array.Length > 64) return $"[array:{array.Length}]";
        return value;
    }

    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sanitized = SensitiveHeader.Replace(value, "$1: [REDACTED]");
        sanitized = SecretJson.Replace(sanitized, "$1\\\"[REDACTED]\\\"");
        sanitized = SecretAssignment.Replace(sanitized, "$1=[REDACTED]");
        return sanitized.Length <= 16000 ? sanitized : sanitized[..16000] + "… [truncated]";
    }
}