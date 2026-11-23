using System.Text.Json;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Services;

// Canonical, semantic fingerprint used only for the "unsaved flow changes" guard.
// Volatile fields (ids created for edges, timestamps, positions, server status) are ignored;
// nodes and edges are sorted so load/save round-trips do not look like edits.
public static class FlowDraftSignature
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false
    };

    public static string Create(IntegrationFlowDto? flow)
    {
        if (flow == null) return string.Empty;

        var nodes = (flow.Nodes ?? new List<IntegrationStepDto>())
            .OrderBy(node => node.Id)
            .Select(node => new
            {
                node.Id,
                node.StepType,
                node.NodeName,
                node.EndpointUrl,
                node.HttpMethod,
                node.UrlMode,
                node.AuthType,
                node.AuthToken,
                node.AuthUsername,
                node.AuthPassword,
                node.AuthConfigJson,
                node.ConnectionId,
                node.MappingCode,
                node.UrlCode,
                node.PreFlightCode,
                node.PostFlightCode,
                node.StepConfig
            })
            .ToList();

        var edges = (flow.Edges ?? new List<IntegrationEdgeDto>())
            .OrderBy(edge => edge.SourceNodeId)
            .ThenBy(edge => edge.SourcePortId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetNodeId)
            .ThenBy(edge => edge.TargetPortId, StringComparer.Ordinal)
            .Select(edge => new
            {
                edge.SourceNodeId,
                edge.TargetNodeId,
                edge.SourcePortId,
                edge.TargetPortId,
                edge.Condition,
                edge.Order
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            flow.Id,
            flow.IntegrationId,
            Name = Normalize(flow.Name),
            Description = Normalize(flow.Description),
            PersistedStateJson = Normalize(flow.PersistedStateJson),
            HelperCode = Normalize(flow.HelperCode),
            TriggerType = Normalize(flow.TriggerType),
            CronExpression = Normalize(flow.CronExpression),
            flow.RunAt,
            flow.AllowPostReplay,
            flow.DefaultDeadLetterResyncStepId,
            Nodes = nodes,
            Edges = edges
        }, Options);
    }

    private static string Normalize(string? value) => value ?? string.Empty;
}
