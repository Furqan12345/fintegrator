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
