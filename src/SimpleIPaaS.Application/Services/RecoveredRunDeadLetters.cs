using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

// A run whose Engine died is failed by stale-run recovery, but its flow state died with the process, so it had no
// dead letter: no alert, no replay. This writes one, with the flow state rebuilt from what was saved: the run's
// start state (trigger payload, or a Fan Out child's snapshot plus item) and each finished card's output.
//
// The rebuilt state is exact for most cards. Two cases are approximate and the entry says so: an HTTP card with a
// post-flight script (flowState got the script's result, the step saved the raw response) and For Each bodies
// (flowState held per-iteration lists). The entry is never replayed automatically: the card the Engine was running
// may already have sent its request.
public sealed class RecoveredRunDeadLetters
{
    private readonly IExecutionRepository _executions;
    private readonly IIntegrationRepository _flows;
    private readonly ILogger<RecoveredRunDeadLetters> _logger;

    public RecoveredRunDeadLetters(IExecutionRepository executions, IIntegrationRepository flows, ILogger<RecoveredRunDeadLetters> logger)
    {
        _executions = executions;
        _flows = flows;
        _logger = logger;
    }

    public async Task<DeadLetterEntry?> CreateAsync(Guid executionId, string recoveryMessage)
    {
        var run = await _executions.GetFlowExecutionAsync(executionId);
        if (run == null || run.IsTest)
        {
            return null;
        }

        if ((await _executions.GetDeadLettersByExecutionAsync(executionId)).Any())
        {
            return null; // a card already failed and was dead-lettered: that entry alerts and replays
        }

        var flow = await _flows.GetByIdAsync(run.FlowId);
        if (flow == null)
        {
            return null;
        }

        var steps = (await _executions.GetStepExecutionsAsync(executionId)).OrderBy(step => step.StartedAt).ToList();
        var interrupted = steps.LastOrDefault(step => step.Status == ExecutionStatus.Failed && step.ErrorMessage == recoveryMessage);
        IntegrationStep? anchor;
        string where;
        if (interrupted != null)
        {
            anchor = flow.Nodes.FirstOrDefault(node => node.Id == interrupted.StepId);
            where = $"while card '{interrupted.NodeName}' was running";
        }
        else if (steps.Count == 0)
        {
            var starts = StartNodes(run, flow);
            anchor = starts.Count == 1 ? starts[0] : null;
            where = "before its first card started";
        }
        else
        {
            // Between cards: the next card is not recorded, so the entry points at the last card that finished and the
            // operator picks where to resume from.
            var last = steps[^1];
            anchor = flow.Nodes.FirstOrDefault(node => node.Id == last.StepId);
            where = $"after card '{last.NodeName}' finished, before the next card started";
        }

        if (anchor == null)
        {
            _logger.LogWarning("Recovered run {ExecutionId}: no card to attach a dead letter to; it stays Failed without one", executionId);
            return null;
        }

        var (state, approximate) = await RebuildStateAsync(run, flow, steps.Where(step => step != interrupted).ToList());
        var note = approximate.Count == 0
            ? "The flow state below was rebuilt from the run's saved card outputs."
            : $"The flow state below was rebuilt from the run's saved card outputs; it is approximate for {string.Join(", ", approximate)}. Check it before replaying.";
        var error = $"The Engine stopped {where}. {recoveryMessage} {note} This entry is never replayed automatically.";
        var now = DateTime.UtcNow;
        var entry = new DeadLetterEntry
        {
            FlowExecutionId = run.Id,
            StepId = anchor.Id,
            FlowName = run.FlowName,
            IntegrationName = run.IntegrationName,
            NodeName = string.IsNullOrWhiteSpace(anchor.NodeName) ? anchor.StepType.ToString() : anchor.NodeName,
            Payload = interrupted == null ? string.Empty
                : string.IsNullOrWhiteSpace(interrupted.RequestPayload) ? interrupted.ReceivedInput : interrupted.RequestPayload,
            FlowStateJson = state.ToString(Newtonsoft.Json.Formatting.None),
            PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson,
            ErrorMessage = error,
            ManualReplayOnly = true,
            Status = "Pending",
            CreatedAt = now,
            AttemptHistoryJson = System.Text.Json.JsonSerializer.Serialize(new List<DeadLetterAttempt>
            {
                new() { Attempt = 0, At = now, Error = error, NodeName = anchor.NodeName, Kind = "Failure" }
            }),
        };
        await _executions.AddDeadLetterEntryAsync(entry);
        return entry;
    }

    private async Task<(JObject State, List<string> Approximate)> RebuildStateAsync(FlowExecution run, IntegrationFlow flow, List<StepExecution> finished)
    {
        JObject state;
        if (!string.IsNullOrWhiteSpace(run.StartStepIdsJson))
        {
            state = Parse(await FanOutState.ResolveInitialFlowStateAsync(run, _executions)) as JObject ?? new JObject();
        }
        else
        {
            state = new JObject();
            if (!string.IsNullOrWhiteSpace(run.TriggerPayloadJson))
            {
                state["trigger"] = Parse(run.TriggerPayloadJson);
            }
        }

        var approximate = new List<string>();
        var nodes = flow.Nodes.ToDictionary(node => node.Id);
        var forEachBodies = flow.Nodes
            .Where(node => node.StepType == StepType.ForEach)
            .SelectMany(node => FlowGraphAnalysis.GetForEachBodyNodeIds(flow, node.Id))
            .ToHashSet();
        foreach (var step in finished.Where(step => step.Status is ExecutionStatus.Success or ExecutionStatus.Recovered))
        {
            if (string.IsNullOrWhiteSpace(step.NodeName) || !nodes.TryGetValue(step.StepId, out var node))
            {
                continue;
            }

            if (forEachBodies.Contains(node.Id))
            {
                if (!approximate.Contains($"'{step.NodeName}' (For Each body)")) approximate.Add($"'{step.NodeName}' (For Each body)");
                state[step.NodeName] = Parse(step.ResponsePayload);
                continue;
            }

            switch (node.StepType)
            {
                // Pass-through cards hand their input on unchanged.
                case StepType.Email:
                case StepType.CrossReferenceStore:
                    state[step.NodeName] = Parse(step.ReceivedInput);
                    break;
                case StepType.Debug:
                    state[step.NodeName] = new JObject { ["inspectedAtUtc"] = step.CompletedAt ?? step.StartedAt };
                    break;
                case StepType.HttpAction when !string.IsNullOrWhiteSpace(node.PostFlightCode) && step.HttpStatusCode is >= 200 and < 300:
                    approximate.Add($"'{step.NodeName}' (post-flight script)");
                    state[step.NodeName] = Parse(step.ResponsePayload);
                    break;
                default:
                    state[step.NodeName] = Parse(step.ResponsePayload);
                    break;
            }
        }

        return (state, approximate);
    }

    private static List<IntegrationStep> StartNodes(FlowExecution run, IntegrationFlow flow)
    {
        if (!string.IsNullOrWhiteSpace(run.StartStepIdsJson))
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(run.StartStepIdsJson) ?? Array.Empty<Guid>();
            return flow.Nodes.Where(node => ids.Contains(node.Id)).ToList();
        }

        return flow.Nodes.Where(node => flow.Edges.All(edge => edge.TargetNodeId != node.Id)).ToList();
    }

    // Same rules as the executor: JSON when it parses, otherwise the raw text; empty output is an empty object.
    private static JToken Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JObject();
        try
        {
            return JToken.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return text;
        }
    }
}
