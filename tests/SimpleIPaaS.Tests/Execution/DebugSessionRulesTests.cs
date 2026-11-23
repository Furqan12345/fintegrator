using SimpleIPaaS.Client.Services;

namespace SimpleIPaaS.Tests.Execution;

public sealed class DebugSessionRulesTests
{
    [Fact]
    public void SuppliedParentUnlocksNormalDownstreamCard()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var incoming = new List<DebugGraph.DebugEdge>
        {
            new(source, target, "right", "left")
        };
        var sessions = new Dictionary<Guid, DebugNodeSession>
        {
            [source] = new() { NodeId = source, State = DebugNodeState.Supplied, Output = "{\"ok\":true}" },
            [target] = new() { NodeId = target }
        };
        var graph = DebugGraph.Build(new[] { source, target }, incoming);

        Assert.True(DebugSessionRules.IsUnlocked(graph.Incoming[target], sessions));
        Assert.True(DebugSessionRules.IsRunnable(sessions[target], graph.Incoming[target], sessions));
    }

    [Fact]
    public void SuppliedBranchFalseUnlocksFalsePathOnly()
    {
        var branch = Guid.NewGuid();
        var trueTarget = Guid.NewGuid();
        var falseTarget = Guid.NewGuid();
        var edges = new List<DebugGraph.DebugEdge>
        {
            new(branch, trueTarget, "true", "left"),
            new(branch, falseTarget, "false", "left")
        };
        var graph = DebugGraph.Build(new[] { branch, trueTarget, falseTarget }, edges);
        var sessions = new Dictionary<Guid, DebugNodeSession>
        {
            [branch] = new() { NodeId = branch, State = DebugNodeState.Supplied, ActivePortId = "false" },
            [trueTarget] = new() { NodeId = trueTarget },
            [falseTarget] = new() { NodeId = falseTarget }
        };

        Assert.False(DebugSessionRules.IsUnlocked(graph.Incoming[trueTarget], sessions));
        Assert.True(DebugSessionRules.IsUnlocked(graph.Incoming[falseTarget], sessions));
    }

    [Fact]
    public void SuppliedCardCanBeReRunOrReSupplied()
    {
        var node = Guid.NewGuid();
        var graph = DebugGraph.Build(new[] { node }, Array.Empty<DebugGraph.DebugEdge>());
        var sessions = new Dictionary<Guid, DebugNodeSession>
        {
            [node] = new() { NodeId = node, State = DebugNodeState.Supplied }
        };

        Assert.True(DebugSessionRules.IsRunnable(sessions[node], graph.Incoming[node], sessions));
    }

    [Fact]
    public void FlowStateOrderingFollowsCompletionAndMovesReRunsToTheEnd()
    {
        var first = new DebugNodeSession { NodeId = Guid.NewGuid(), NodeName = "First", State = DebugNodeState.Success, Output = "{}", Sequence = 1 };
        var second = new DebugNodeSession { NodeId = Guid.NewGuid(), NodeName = "Second", State = DebugNodeState.Supplied, Output = "{}", Sequence = 2 };
        var third = new DebugNodeSession { NodeId = Guid.NewGuid(), NodeName = "Third", State = DebugNodeState.Success, Output = "{}", Sequence = 3 };

        var ordered = DebugStateOrdering.Ordered(new[] { third, first, second });
        Assert.Equal(new[] { "First", "Second", "Third" }, ordered.Select(item => item.NodeName));

        first.Sequence = 4; // re-running First moves it to the end
        ordered = DebugStateOrdering.Ordered(new[] { third, first, second });
        Assert.Equal(new[] { "Second", "Third", "First" }, ordered.Select(item => item.NodeName));
    }
}
