using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

// Debug Mode is the only place a user can execute a single card, so these tests pin the two
// contracts the designer relies on: the card behaves like it does in a production run, and
// exploring a flow never mutates durable state.
public sealed class FlowExecutorDebugNodeTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private FlowExecutor CreateExecutor() => new(
        _flows,
        new UnreachableTransportEngine(),
        new StubCodeExecutionService(),
        _executions,
        new StubCrossReferenceRepository(),
        new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

    private sealed class StubCodeExecutionService : IAdvancedCodeExecutionService
    {
        public Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult($"mapped:{csharpCode}");

        public Task<string> ExecuteEmailAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult("{}");

        public Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson, string? helperCode = null) =>
            Task.FromResult($"post:{csharpCode}");

        public Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult($"url:{csharpCode}");

        public Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult(false);

        public Task ValidateAsync(string csharpCode, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static IntegrationStep Node(string name, StepType type, string mappingCode = "") => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = type,
        MappingCode = mappingCode
    };

    private FlowExecution SeedExecution(IntegrationFlow flow)
    {
        _flows.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        _executions.Seed(execution);
        return execution;
    }

    [Fact]
    public async Task ExecuteDebugNodeAsync_RunsOnlyTheSelectedCard()
    {
        var fetch = Node("Fetch", StepType.Mapping, "fetch-code");
        var next = Node("Next", StepType.Mapping, "next-code");
        var flow = new IntegrationFlow { Name = "Debug" };
        flow.Nodes.Add(fetch);
        flow.Nodes.Add(next);
        flow.Edges.Add(new IntegrationEdge { SourceNodeId = fetch.Id, TargetNodeId = next.Id, SourcePortId = "right", TargetPortId = "left" });
        SeedExecution(flow);

        var result = await CreateExecutor().ExecuteDebugNodeAsync(
            flow.Id, fetch.Id, new JObject(), null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Null(result.Error);
        Assert.Equal("mapped:fetch-code", result.Output);
        Assert.Equal(fetch.Id, result.StepId);
        Assert.NotNull(result.StepExecutionId);

        // The downstream card must not have run: Debug Mode advances one click at a time.
        Assert.DoesNotContain(_executions.StepExecutions, step => step.NodeName == "Next");
    }

    [Fact]
    public async Task ExecuteDebugNodeAsync_PassesAccumulatedUpstreamOutputAsInput()
    {
        var fetch = Node("Fetch", StepType.Mapping, "fetch-code");
        var map = Node("Map", StepType.Mapping, "map-code");
        var flow = new IntegrationFlow { Name = "Debug input" };
        flow.Nodes.Add(fetch);
        flow.Nodes.Add(map);
        flow.Edges.Add(new IntegrationEdge { SourceNodeId = fetch.Id, TargetNodeId = map.Id, SourcePortId = "right", TargetPortId = "left" });
        SeedExecution(flow);

        var executor = CreateExecutor();
        var fetchResult = await executor.ExecuteDebugNodeAsync(flow.Id, fetch.Id, new JObject(), null, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Success, fetchResult.Status);

        // The designer stores an output as JSON when it parses and as a raw string otherwise,
        // matching what the executor does with flowStateContext[node.NodeName].
        JToken Parsed(string payload)
        {
            try
            {
                return JToken.Parse(payload);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return payload;
            }
        }

        var flowState = new JObject { ["Fetch"] = Parsed(fetchResult.Output!) };
        var mapResult = await executor.ExecuteDebugNodeAsync(flow.Id, map.Id, flowState, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, mapResult.Status);
        Assert.Contains("fetch-code", mapResult.Input);
    }

    [Fact]
    public async Task ExecuteDebugNodeAsync_ReportsFailureWithoutDeadLettering()
    {
        // A Branch with no mapping code evaluates to false; the failure path here is an HTTP node
        // with no transport, which throws rather than returning a status.
        var failing = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Broken",
            StepType = StepType.HttpAction,
            EndpointUrl = "https://example.invalid/orders"
        };
        var flow = new IntegrationFlow { Name = "Debug failure" };
        flow.Nodes.Add(failing);
        SeedExecution(flow);

        var result = await CreateExecutor().ExecuteDebugNodeAsync(
            flow.Id, failing.Id, new JObject(), null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        // A human is watching this run: it must not queue a dead letter for the ops team.
        Assert.Empty(_executions.DeadLetters);
    }

    [Fact]
    public async Task ExecuteDebugNodeAsync_DoesNotPersistStateCardWrites()
    {
        var state = Node("Cursor", StepType.PersistedState, "\"{\\\"page\\\":2}\"");
        var flow = new IntegrationFlow { Name = "Debug state", PersistedStateJson = "{\"page\":1}" };
        flow.Nodes.Add(state);
        SeedExecution(flow);

        var result = await CreateExecutor().ExecuteDebugNodeAsync(
            flow.Id, state.Id, new JObject(), flow.PersistedStateJson, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);

        // Exploring a flow must never advance its durable cursor.
        Assert.Equal("{\"page\":1}", flow.PersistedStateJson);
    }

    [Fact]
    public async Task ExecuteDebugNodeAsync_UnknownNodeIsRejected()
    {
        var flow = new IntegrationFlow { Name = "Missing node" };
        SeedExecution(flow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateExecutor().ExecuteDebugNodeAsync(
            flow.Id, Guid.NewGuid(), new JObject(), null, CancellationToken.None));
    }
}

// The dependency gate lives in the client, and these are the rules the badge/button state follows.
public sealed class DebugGraphGatingTests
{
    private static DebugGraph BuildChain()
    {
        var root = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var leaf = Guid.NewGuid();
        var graph = DebugGraph.Build(
            new[] { root, middle, leaf },
            new[]
            {
                new DebugGraph.DebugEdge(root, middle, "right", "left"),
                new DebugGraph.DebugEdge(middle, leaf, "right", "left")
            });

        return graph;
    }

    [Fact]
    public void RootsAreTheOnlyInitiallyRunnableNodes()
    {
        var graph = BuildChain();
        var root = graph.NodeIds.First(id => graph.IsRoot(id));
        var other = graph.NodeIds.First(id => !graph.IsRoot(id));

        Assert.True(graph.IsRoot(root));
        Assert.False(graph.IsRoot(other));
    }

    [Fact]
    public void DescendantsCoversTheWholeDownstreamPath()
    {
        var graph = BuildChain();
        var root = graph.NodeIds.First(id => graph.IsRoot(id));

        Assert.Equal(2, graph.Descendants(root).Count);
    }

    [Fact]
    public void BranchOnlyActivatesThePortItEvaluated()
    {
        var branch = Guid.NewGuid();
        var onTrue = Guid.NewGuid();
        var onFalse = Guid.NewGuid();
        var graph = DebugGraph.Build(
            new[] { branch, onTrue, onFalse },
            new[]
            {
                new DebugGraph.DebugEdge(branch, onTrue, "true", "left"),
                new DebugGraph.DebugEdge(branch, onFalse, "false", "left")
            });

        var active = graph.ActiveOutgoing(branch, "false").ToList();

        Assert.Single(active);
        Assert.Equal(onFalse, active[0].TargetNodeId);
    }

    [Fact]
    public void AllOutgoingEdgesActivateWhenNoPortIsSpecified()
    {
        var branch = Guid.NewGuid();
        var onTrue = Guid.NewGuid();
        var onFalse = Guid.NewGuid();
        var graph = DebugGraph.Build(
            new[] { branch, onTrue, onFalse },
            new[]
            {
                new DebugGraph.DebugEdge(branch, onTrue, "true", "left"),
                new DebugGraph.DebugEdge(branch, onFalse, "false", "left")
            });

        Assert.Equal(2, graph.ActiveOutgoing(branch, null).Count());
    }

    // The designer's own regression: after a successful run the card must expose a completed state,
    // because that is what renders the check badge and the View Logs button. RunDebugNodeAsync used
    // to write only the session, leaving the node stuck on Idle with no way to open its logs.
    [Fact]
    public void CompletedSessionMakesItsNodeLoggableAndRunnableAgain()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var graph = DebugGraph.Build(
            new[] { root, child },
            new[] { new DebugGraph.DebugEdge(root, child, "right", "left") });

        var sessions = new Dictionary<Guid, DebugNodeSession>
        {
            [root] = new DebugNodeSession { NodeId = root, State = DebugNodeState.Success },
            [child] = new DebugNodeSession { NodeId = child }
        };

        // A root is always unlocked, and a completed card must report itself as re-runnable.
        Assert.True(DebugSessionRules.IsUnlocked(graph.Incoming[root], sessions));
        Assert.True(DebugSessionRules.IsRunnable(sessions[root], graph.Incoming[root], sessions));
        Assert.True(DebugSessionRules.IsUnlocked(graph.Incoming[child], sessions));
        Assert.True(DebugSessionRules.IsRunnable(sessions[child], graph.Incoming[child], sessions));
    }

    [Fact]
    public void DownstreamStaysLockedUntilItsParentSucceeds()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var graph = DebugGraph.Build(
            new[] { root, child },
            new[] { new DebugGraph.DebugEdge(root, child, "right", "left") });

        var sessions = new Dictionary<Guid, DebugNodeSession>
        {
            [root] = new DebugNodeSession { NodeId = root },
            [child] = new DebugNodeSession { NodeId = child }
        };

        Assert.False(DebugSessionRules.IsUnlocked(graph.Incoming[child], sessions));

        sessions[root].State = DebugNodeState.Error;
        Assert.False(DebugSessionRules.IsUnlocked(graph.Incoming[child], sessions));

        sessions[root].State = DebugNodeState.Running;
        Assert.False(DebugSessionRules.IsUnlocked(graph.Incoming[child], sessions));
    }

    // The exact bug behind "after running, no View Logs button": RunDebugNodeAsync updated the
    // session but never the node, and the canvas renders the node.
    [Theory]
    [InlineData(DebugNodeState.Success, true)]
    [InlineData(DebugNodeState.Error, true)]
    [InlineData(DebugNodeState.Running, false)]
    [InlineData(DebugNodeState.Idle, false)]
    public void CompletedSessionIsProjectedOntoTheNode(DebugNodeState state, bool expectLogs)
    {
        var node = new SimpleIPaaS.Client.Components.Nodes.IntegrationNodeModel(
            new SimpleIPaaS.Shared.Models.IntegrationStepDto { StepType = "Mapping", NodeName = "Map" });

        node.ApplyDebugSession(new DebugNodeSession { NodeId = node.StepConfig.Id, State = state });

        Assert.Equal(state, node.DebugState);
        Assert.Equal(expectLogs, node.HasDebugLogs);
    }

    // A converging node must start on the branch that actually fired instead of waiting forever on
    // the port the Branch skipped.
    [Fact]
    public void ConvergingNodeUnlocksOnTheBranchThatFired()
    {
        var branch = Guid.NewGuid();
        var join = Guid.NewGuid();
        var graph = DebugGraph.Build(
            new[] { branch, join },
            new[]
            {
                new DebugGraph.DebugEdge(branch, join, "true", "left"),
                new DebugGraph.DebugEdge(branch, join, "false", "left")
            });

        var sessions = new Dictionary<Guid, DebugNodeSession>
        {
            [branch] = new DebugNodeSession { NodeId = branch, State = DebugNodeState.Success, ActivePortId = "false" },
            [join] = new DebugNodeSession { NodeId = join }
        };

        Assert.True(DebugSessionRules.IsUnlocked(graph.Incoming[join], sessions));
    }
}


