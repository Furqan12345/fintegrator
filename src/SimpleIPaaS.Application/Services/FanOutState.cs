using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

// Builds the flow state a Fan Out child run starts from: the parent's shared snapshot plus the child's item.
public static class FanOutState
{
    // The start state of a run that begins part-way through the flow (StartStepIdsJson). Null when the run is a
    // Fan Out child whose snapshot no longer exists (its parent run was deleted).
    public static async Task<string?> ResolveInitialFlowStateAsync(FlowExecution execution, IExecutionRepository repository)
    {
        if (execution.FanOutSnapshotId is not Guid snapshotId)
        {
            return execution.InitialFlowStateJson ?? "{}";
        }

        var snapshot = await repository.GetFanOutSnapshotAsync(snapshotId);
        return snapshot == null ? null : BuildChildState(snapshot, execution.FanOutItemJson);
    }

    public static string BuildChildState(FanOutSnapshot snapshot, string? itemJson)
    {
        var state = ParseObject(snapshot.FlowStateJson);
        var item = ParseToken(itemJson);
        state[string.IsNullOrWhiteSpace(snapshot.ItemVariable) ? "item" : snapshot.ItemVariable] = item;
        if (!string.IsNullOrWhiteSpace(snapshot.NodeName))
        {
            state[snapshot.NodeName] = item.DeepClone();
        }

        return state.ToString(Newtonsoft.Json.Formatting.None);
    }

    // A Fan Out child's full flow state as a difference from its snapshot's state: the top-level keys that were added
    // or changed, and the snapshot keys that are gone. The snapshot never changes, so the difference is exact whoever
    // made the state (the failing card, or someone editing it before a replay). Null when the state is not a JSON
    // object: it is then stored in full.
    public static (string ChangedJson, string RemovedKeysJson)? Diff(string snapshotStateJson, string fullStateJson)
    {
        JObject full;
        try
        {
            if (ParseVerbatim(string.IsNullOrWhiteSpace(fullStateJson) ? "{}" : fullStateJson) is not JObject parsed) return null;
            full = parsed;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }

        var snapshot = ParseObjectVerbatim(snapshotStateJson);
        var changed = new JObject();
        foreach (var property in full.Properties())
        {
            if (!snapshot.TryGetValue(property.Name, StringComparison.Ordinal, out var before) || !JToken.DeepEquals(before, property.Value))
            {
                changed[property.Name] = property.Value.DeepClone();
            }
        }

        var removed = new JArray(snapshot.Properties().Select(property => property.Name).Where(name => full.Property(name, StringComparison.Ordinal) == null));
        return (changed.ToString(Newtonsoft.Json.Formatting.None), removed.ToString(Newtonsoft.Json.Formatting.None));
    }

    // The inverse of Diff: the snapshot's state with the changed keys applied and the removed keys taken out.
    public static string Apply(string snapshotStateJson, string? changedJson, string? removedKeysJson)
    {
        var state = ParseObjectVerbatim(snapshotStateJson);
        foreach (var property in ParseObjectVerbatim(changedJson).Properties())
        {
            state[property.Name] = property.Value;
        }

        if (!string.IsNullOrWhiteSpace(removedKeysJson))
        {
            try
            {
                if (JToken.Parse(removedKeysJson) is JArray removed)
                {
                    foreach (var name in removed.Values<string>())
                    {
                        if (name != null) state.Remove(name);
                    }
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // A malformed list removes nothing.
            }
        }

        return state.ToString(Newtonsoft.Json.Formatting.None);
    }

    // Dates stay the strings they were (Newtonsoft would otherwise turn them into DateTime values and may write them
    // back differently), so a key nobody changed comes back byte for byte.
    private static JToken ParseVerbatim(string json)
    {
        using var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }

    private static JObject ParseObjectVerbatim(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JObject();
        try
        {
            return ParseVerbatim(json) as JObject ?? new JObject();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new JObject();
        }
    }

    private static JObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JObject();
        try
        {
            return JToken.Parse(json) as JObject ?? new JObject();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new JObject();
        }
    }

    private static JToken ParseToken(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JValue.CreateNull();
        try
        {
            return JToken.Parse(json);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return json;
        }
    }
}
