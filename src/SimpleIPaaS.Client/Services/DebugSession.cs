using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleIPaaS.Client.Services;

public enum DebugNodeState
{
    Idle,
    Running,
    Success,
    Error,
    Supplied
}

public sealed class DebugNodeSession
{
    public Guid NodeId { get; init; }
    public string NodeName { get; init; } = string.Empty;
    public DebugNodeState State { get; set; } = DebugNodeState.Idle;
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? StepExecutionId { get; set; }
    public string? ActivePortId { get; set; }
    public DateTime CompletedAtUtc { get; set; }

    // Monotonic completion/supply order within one Debug Mode session. Re-running or re-supplying
    // a card assigns a new number, moving it to the end of the Flow state.
    public int Sequence { get; set; }
}

public static class DebugStateOrdering
{
    public static IReadOnlyList<DebugNodeSession> Ordered(
        IEnumerable<DebugNodeSession> sessions,
        Guid? excludeNodeId = null) =>
        sessions
            .Where(session => session.Sequence > 0
                && session.State is DebugNodeState.Success or DebugNodeState.Supplied
                && session.NodeId != excludeNodeId
                && !string.IsNullOrWhiteSpace(session.NodeName)
                && !string.IsNullOrWhiteSpace(session.Output))
            .OrderBy(session => session.Sequence)
            .ThenBy(session => session.NodeName, StringComparer.Ordinal)
            .ToList();
}

public sealed class DebugGraph
{
    public HashSet<Guid> NodeIds { get; } = new();
    public Dictionary<Guid, List<DebugEdge>> Outgoing { get; } = new();
    public Dictionary<Guid, List<DebugEdge>> Incoming { get; } = new();

    public sealed record DebugEdge(Guid SourceNodeId, Guid TargetNodeId, string SourcePortId, string TargetPortId);

    public static DebugGraph Build(IEnumerable<Guid> nodeIds, IEnumerable<DebugEdge> edges)
    {
        var graph = new DebugGraph();
        foreach (var nodeId in nodeIds)
        {
            graph.NodeIds.Add(nodeId);
            graph.Outgoing[nodeId] = new List<DebugEdge>();
            graph.Incoming[nodeId] = new List<DebugEdge>();
        }

        foreach (var edge in edges)
        {
            if (!graph.NodeIds.Contains(edge.SourceNodeId) || !graph.NodeIds.Contains(edge.TargetNodeId))
            {
                continue;
            }

            graph.Outgoing[edge.SourceNodeId].Add(edge);
            graph.Incoming[edge.TargetNodeId].Add(edge);
        }

        return graph;
    }

    public bool IsRoot(Guid nodeId) => !Incoming.TryGetValue(nodeId, out var incoming) || incoming.Count == 0;

    // A Branch only activates the port it evaluated; every other card activates all of its edges.
    public IEnumerable<DebugEdge> ActiveOutgoing(Guid nodeId, string? activePortId)
    {
        if (!Outgoing.TryGetValue(nodeId, out var edges))
        {
            return Enumerable.Empty<DebugEdge>();
        }

        if (string.IsNullOrEmpty(activePortId))
        {
            return edges;
        }

        return edges.Where(edge => string.Equals(edge.SourcePortId, activePortId, StringComparison.OrdinalIgnoreCase));
    }

    // Every downstream node of the given node, excluding itself.
    public HashSet<Guid> Descendants(Guid nodeId)
    {
        var result = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        pending.Enqueue(nodeId);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var edge in Outgoing.TryGetValue(current, out var edges) ? edges : Enumerable.Empty<DebugEdge>())
            {
                if (result.Add(edge.TargetNodeId))
                {
                    pending.Enqueue(edge.TargetNodeId);
                }
            }
        }

        return result;
    }
}

// The dependency gate for Debug Mode. A node may run when every upstream parent has succeeded and
// at least one of its incoming edges actually delivered output.
public static class DebugSessionRules
{
    public static bool IsUnlocked(
        IReadOnlyList<DebugGraph.DebugEdge> incoming,
        IReadOnlyDictionary<Guid, DebugNodeSession> sessions)
    {
        if (incoming.Count == 0)
        {
            return true;
        }

        var delivered = false;
        foreach (var edge in incoming)
        {
            if (!sessions.TryGetValue(edge.SourceNodeId, out var parent)
                || parent.State is not (DebugNodeState.Success or DebugNodeState.Supplied))
            {
                return false;
            }

            // A Branch activates exactly one port. An edge from the port it skipped can never fire,
            // so it neither unlocks the node nor keeps it waiting.
            var edgeActive = string.IsNullOrEmpty(parent.ActivePortId) ||
                string.Equals(parent.ActivePortId, edge.SourcePortId, StringComparison.OrdinalIgnoreCase);
            delivered |= edgeActive;
        }

        return delivered;
    }

    // A succeeded or failed card stays re-runnable: re-running it is how an operator replaces its
    // output, and doing so invalidates everything downstream.
    public static bool IsRunnable(
        DebugNodeSession session,
        IReadOnlyList<DebugGraph.DebugEdge> incoming,
        IReadOnlyDictionary<Guid, DebugNodeSession> sessions) =>
        session.State != DebugNodeState.Running && IsUnlocked(incoming, sessions);
}
