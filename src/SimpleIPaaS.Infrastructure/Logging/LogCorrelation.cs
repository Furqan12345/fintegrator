using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimpleIPaaS.Infrastructure.Logging;

// Harvests correlation ids out of ILogger scopes and structured message state.
//
// The Engine already logs with structured properties — FlowExecutor writes
// "Execution {ExecutionId}: running node {NodeName}", FlowExecutionWorker opens scopes
// carrying ExecutionId/FlowId/TenantId — so the values land in dedicated columns for
// free, without any call site having to know this sink exists.
internal struct LogCorrelation
{
    public Guid? TenantId;
    public Guid? FlowExecutionId;
    public Guid? FlowId;
    public string? NodeName;

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

    private void Apply(string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        if (Matches(key, "ExecutionId") || Matches(key, "FlowExecutionId"))
        {
            FlowExecutionId = AsGuid(value) ?? FlowExecutionId;
        }
        else if (Matches(key, "FlowId"))
        {
            FlowId = AsGuid(value) ?? FlowId;
        }
        else if (Matches(key, "TenantId"))
        {
            TenantId = AsGuid(value) ?? TenantId;
        }
        else if (Matches(key, "NodeName"))
        {
            var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text))
            {
                NodeName = text;
            }
        }
    }

    private static bool Matches(string key, string name) =>
        string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static Guid? AsGuid(object value)
    {
        if (value is Guid guid)
        {
            return guid == Guid.Empty ? null : guid;
        }

        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(text) && Guid.TryParse(text, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        return null;
    }
}
