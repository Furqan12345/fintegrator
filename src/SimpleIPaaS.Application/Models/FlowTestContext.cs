using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace SimpleIPaaS.Application.Models;

public sealed class FlowTestDefinition
{
    public int Version { get; set; } = 1;
    public string TriggerPayloadJson { get; set; } = string.Empty;
    public Dictionary<string, List<FlowHttpResponseOverride>> HttpResponses { get; set; } = new();
    public List<FlowCrossReferenceFixture> CrossReferenceRows { get; set; } = new();
    public Dictionary<string, string> Variables { get; set; } = new();
    public List<FlowTestAssertion> Assertions { get; set; } = new();
    public List<FlowTestCardOverride> CardOverrides { get; set; } = new();
    public string ResponseExhaustionBehavior { get; set; } = "RepeatFinalResponse";
}

public sealed class FlowTestCardOverride
{
    public Guid CardId { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string Mode { get; set; } = "Run";
    public string OutputFormat { get; set; } = "Json";
    public string OutputJson { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class FlowHttpResponseOverride
{
    public int StatusCode { get; set; } = 200;
    public string ResponseBody { get; set; } = "{}";
    public string ResponseFormat { get; set; } = "Json";
    public Dictionary<string, string[]> Headers { get; set; } = new();
}

public sealed class FlowCrossReferenceFixture
{
    public string ListName { get; set; } = string.Empty;
    public string KeyValue { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
}

public sealed class FlowTestAssertion
{
    public string Type { get; set; } = "ExecutionStatus";
    public Guid? CardId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string Field { get; set; } = "Status";
    public string Path { get; set; } = string.Empty;
    public string Operator { get; set; } = "Equals";
    public string ExpectedValue { get; set; } = "Success";
    public string InvocationScope { get; set; } = "Every";
    public int? InvocationNumber { get; set; }
}

public sealed record FlowTestHttpResponse(int StatusCode, string Response, IReadOnlyDictionary<string, string[]> Headers);

public sealed class FlowTestContext
{
    private readonly Dictionary<Guid, Queue<FlowHttpResponseOverride>> _httpResponses;
    private readonly Dictionary<Guid, int> _httpResponseCounts = new();
    private readonly Dictionary<string, HashSet<string>> _crossReferenceKeys;
    private readonly Dictionary<string, string> _variables;
    private readonly DateTime _startedAt;
    private readonly object _fixtureLock = new();

    public FlowTestDefinition Definition { get; }
    public Guid ExecutionId { get; }

    private FlowTestContext(FlowTestDefinition definition, Guid executionId)
    {
        Definition = definition;
        ExecutionId = executionId;
        _startedAt = DateTime.UtcNow;
        _variables = new Dictionary<string, string>(definition.Variables ?? new(), StringComparer.OrdinalIgnoreCase);
        _httpResponses = new Dictionary<Guid, Queue<FlowHttpResponseOverride>>();
        _crossReferenceKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fixture in definition.CrossReferenceRows ?? new())
        {
            if (string.IsNullOrWhiteSpace(fixture.ListName) || string.IsNullOrWhiteSpace(fixture.KeyValue)) continue;
            if (!_crossReferenceKeys.TryGetValue(fixture.ListName, out var keys))
                _crossReferenceKeys[fixture.ListName] = keys = new HashSet<string>(StringComparer.Ordinal);
            keys.Add(Resolve(fixture.KeyValue));
        }
    }

    public static FlowTestContext FromJson(string? json, Guid executionId)
    {
        var definition = string.IsNullOrWhiteSpace(json)
            ? new FlowTestDefinition()
            : JsonConvert.DeserializeObject<FlowTestDefinition>(json) ?? new FlowTestDefinition();
        return new FlowTestContext(definition, executionId);
    }

    public string Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        return Regex.Replace(value, "\\{\\{\\s*([^{}]+?)\\s*\\}\\}", match =>
        {
            var token = match.Groups[1].Value.Trim();
            if (_variables.TryGetValue(token, out var variable)) return variable;
            if (token.Equals("run.id", StringComparison.OrdinalIgnoreCase)) return ExecutionId.ToString();
            if (token.Equals("run.startedAt", StringComparison.OrdinalIgnoreCase)) return _startedAt.ToString("O", CultureInfo.InvariantCulture);
            if (token.Equals("uuid", StringComparison.OrdinalIgnoreCase)) return Guid.NewGuid().ToString();
            if (token.Equals("now", StringComparison.OrdinalIgnoreCase)) return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return match.Value;
        });
    }

    public string ResolveTriggerPayload(string? fallback)
        => string.IsNullOrWhiteSpace(Definition.TriggerPayloadJson) ? fallback ?? string.Empty : Resolve(Definition.TriggerPayloadJson);

    public bool TryGetCardOverride(Guid cardId, out FlowTestCardOverride cardOverride)
    {
        cardOverride = (Definition.CardOverrides ?? new()).FirstOrDefault(item => item.CardId == cardId)!;
        return cardOverride != null && string.Equals(cardOverride.Mode, "Supply", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasHttpFixture(Guid stepId) => (Definition.HttpResponses ?? new()).Any(item => Guid.TryParse(item.Key, out var id) && id == stepId && item.Value != null && item.Value.Count > 0);

    public bool TryTakeHttpResponse(Guid stepId, out FlowTestHttpResponse response)
    {
        response = default!;
        lock (_fixtureLock)
        {
            var pair = (Definition.HttpResponses ?? new()).FirstOrDefault(item => Guid.TryParse(item.Key, out var id) && id == stepId);
            var definitions = pair.Value;
            if (definitions == null || definitions.Count == 0) return false;
            if (!_httpResponses.TryGetValue(stepId, out var queue))
            {
                queue = new Queue<FlowHttpResponseOverride>(definitions);
                _httpResponses[stepId] = queue;
            }

            _httpResponseCounts.TryGetValue(stepId, out var count);
            _httpResponseCounts[stepId] = count + 1;
            if (queue.Count == 0 && string.Equals(Definition.ResponseExhaustionBehavior, "FailWhenExhausted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"HTTP card fixture was exhausted after {count} response(s).");

            var selected = queue.Count > 0 ? queue.Dequeue() : definitions[^1];
            response = new FlowTestHttpResponse(selected.StatusCode, Resolve(selected.ResponseBody),
                (selected.Headers ?? new()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
            return true;
        }
    }

    public bool ContainsCrossReferenceKey(string listName, string key)
    {
        lock (_fixtureLock)
            return _crossReferenceKeys.TryGetValue(listName, out var keys) && keys.Contains(Resolve(key));
    }

    public void StoreCrossReferenceKeys(string listName, IEnumerable<string> keys)
    {
        lock (_fixtureLock)
        {
            if (!_crossReferenceKeys.TryGetValue(listName, out var stored))
                _crossReferenceKeys[listName] = stored = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in keys.Where(key => !string.IsNullOrWhiteSpace(key))) stored.Add(Resolve(key));
        }
    }
}