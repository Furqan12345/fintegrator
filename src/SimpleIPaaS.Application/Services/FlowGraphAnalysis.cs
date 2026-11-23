using System;
using System.Collections.Generic;
using System.Linq;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

// Graph queries shared by the executor, the dead-letter API and the replay service.
// Keeping loop-body detection in one place guarantees the nodes offered for a resync
// are exactly the ones the executor and the replay validation consider legal.
public static class FlowGraphAnalysis
{
    public static HashSet<Guid> GetAncestorNodeIds(IntegrationFlow flow, Guid nodeId)
    {
        var result = new HashSet<Guid> { nodeId };
        var pending = new Queue<Guid>();
        pending.Enqueue(nodeId);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var edge in flow.Edges.Where(edge => edge.TargetNodeId == current))
            {
                if (result.Add(edge.SourceNodeId))
                {
                    pending.Enqueue(edge.SourceNodeId);
                }
            }
        }

        return result;
    }

    public static HashSet<Guid> GetDownstreamNodeIds(IntegrationFlow flow, Guid startNodeId)
    {
        var result = new HashSet<Guid> { startNodeId };
        var pending = new Queue<Guid>();
        pending.Enqueue(startNodeId);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var edge in flow.Edges.Where(edge => edge.SourceNodeId == current))
            {
                if (result.Add(edge.TargetNodeId))
                {
                    pending.Enqueue(edge.TargetNodeId);
                }
            }
        }

        return result;
    }

    // A node is in a ForEach's iteration body when every predecessor is already in scope,
    // starting from the ForEach itself. The "completed" port is the post-loop activation and
    // is deliberately not seeded into the body.
    public static HashSet<Guid> GetForEachBodyNodeIds(IntegrationFlow flow, Guid forEachNodeId)
    {
        var inScope = new HashSet<Guid> { forEachNodeId };
        var discovered = new HashSet<Guid>();
        var queue = new Queue<Guid>();

        foreach (var edge in flow.Edges.Where(edge =>
                     edge.SourceNodeId == forEachNodeId &&
                     (edge.SourcePortId ?? string.Empty) != "completed"))
        {
            queue.Enqueue(edge.TargetNodeId);
        }

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            if (inScope.Contains(nodeId) || discovered.Contains(nodeId))
            {
                continue;
            }

            discovered.Add(nodeId);

            var predecessors = flow.Edges
                .Where(edge => edge.TargetNodeId == nodeId)
                .Select(edge => edge.SourceNodeId);

            if (!predecessors.All(inScope.Contains))
            {
                continue;
            }

            inScope.Add(nodeId);

            foreach (var edge in flow.Edges.Where(edge => edge.SourceNodeId == nodeId))
            {
                if (!inScope.Contains(edge.TargetNodeId) && !discovered.Contains(edge.TargetNodeId))
                {
                    queue.Enqueue(edge.TargetNodeId);
                }
            }
        }

        inScope.Remove(forEachNodeId);
        return inScope;
    }

    public static HashSet<Guid> GetAllForEachBodyNodeIds(IntegrationFlow flow)
    {
        var result = new HashSet<Guid>();
        foreach (var node in flow.Nodes.Where(node => node.StepType == StepType.ForEach))
        {
            result.UnionWith(GetForEachBodyNodeIds(flow, node.Id));
        }

        return result;
    }

    // For a node inside nested loops this returns the outermost loop, which is the only
    // safe resync point: re-running an inner loop would leave outer iteration state stale.
    public static Guid? GetOutermostForEachNodeId(IntegrationFlow flow, Guid nodeId)
    {
        var containing = flow.Nodes
            .Where(node => node.StepType == StepType.ForEach)
            .Select(node => new { NodeId = node.Id, Body = GetForEachBodyNodeIds(flow, node.Id) })
            .Where(loop => loop.Body.Contains(nodeId))
            .ToList();

        if (containing.Count == 0)
        {
            return null;
        }

        var outermost = containing.FirstOrDefault(loop =>
            !containing.Any(other => other.NodeId != loop.NodeId && other.Body.Contains(loop.NodeId)));
        return outermost?.NodeId ?? containing[0].NodeId;
    }
}
