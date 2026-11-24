using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public class FlowExecutorDagTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private sealed class RecordingTransport : ITransportEngine
    {
        public Task<TransportResponse> DispatchAsync(
            IntegrationStep step,
            string? payload,
            Guid? connectionId = null,
            CancellationToken cancellationToken = default,
            FlowTestContext? testContext = null) =>
            Task.FromResult(new TransportResponse(200, "{\"ok\":true}", new Dictionary<string, string[]>(), step.EndpointUrl));
    }

    private sealed class StubCodeExecutionService : IAdvancedCodeExecutionService
    {
        public Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult($"text:{helperCode}{csharpCode}");

        public Task<string> ExecuteEmailAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult("{}");

        public Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson, string? helperCode = null) =>
            Task.FromResult($"text:{helperCode}{csharpCode}");

        public Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult($"text:{helperCode}{csharpCode}");

        public Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
            Task.FromResult(true);

        public Task ValidateAsync(string csharpCode, System.Threading.CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private FlowExecutor CreateExecutor() => new(
        _flows,
        new UnreachableTransportEngine(),
        new UnreachableCodeExecutionService(),
        _executions,
        new StubCrossReferenceRepository(),
        new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

    private static IntegrationStep Node(string name) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.Debug
    };

    private static IntegrationEdge Edge(IntegrationStep source, IntegrationStep target) => new()
    {
        SourceNodeId = source.Id,
        TargetNodeId = target.Id
    };

    private FlowExecution SeedExecution(IntegrationFlow flow)
    {
        _flows.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        _executions.Seed(execution);
        return execution;
    }

    [Fact]
    public async Task ExecuteFlowAsync_IdentifierAndMessageCardsWriteTransactionColumns()
    {
        var fetch = Node("Fetch");
        var identifier = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "SetIdentifier", StepType = StepType.AddIdentifier, MappingCode = "identifier-code" };
        var success = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "ReportSuccess", StepType = StepType.Success, MappingCode = "success-code" };

        var flow = new IntegrationFlow { Nodes = { fetch, identifier, success } };
        flow.Edges.Add(Edge(fetch, identifier));
        flow.Edges.Add(Edge(identifier, success));

        var execution = SeedExecution(flow);

        var result = await new FlowExecutor(
            _flows,
            new UnreachableTransportEngine(),
            new StubCodeExecutionService(),
            _executions,
            new StubCrossReferenceRepository(),
            new FakeEmailSender(), NullLogger<FlowExecutor>.Instance).ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("text:identifier-code", result.Identifier);
        Assert.Equal("text:success-code", result.Message);
        var identifierStep = _executions.StepExecutions.Single(s => s.NodeName == "SetIdentifier");
        Assert.Equal("text:identifier-code", identifierStep.ResponsePayload);
    }

    [Fact]
    public async Task ExecuteFlowAsync_PassesFlowHelperCodeToNodeScripts()
    {
        var mapper = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Map",
            StepType = StepType.Mapping,
            MappingCode = "mapping-code"
        };
        var flow = new IntegrationFlow
        {
            Name = "Helpers",
            HelperCode = "helper-prelude"
        };
        flow.Nodes.Add(mapper);
        var execution = SeedExecution(flow);

        await new FlowExecutor(
            _flows,
            new UnreachableTransportEngine(),
            new StubCodeExecutionService(),
            _executions,
            new StubCrossReferenceRepository(),
            new FakeEmailSender(), NullLogger<FlowExecutor>.Instance).ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        var step = _executions.StepExecutions.Single(item => item.NodeName == "Map");
        Assert.Equal("text:helper-preludemapping-code", step.ResponsePayload);
    }

    [Fact]
    public async Task ExecuteFlowAsync_PassesFlowHelperCodeToHttpActionScripts()
    {
        var http = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Call",
            StepType = StepType.HttpAction,
            HttpMethod = "GET",
            UrlCode = "url-code",
            PreFlightCode = "preflight-code",
            PostFlightCode = "postflight-code"
        };
        var inspect = Node("Inspect");
        var flow = new IntegrationFlow
        {
            Name = "Helpers",
            HelperCode = "helper-prelude"
        };
        flow.Nodes.Add(http);
        flow.Nodes.Add(inspect);
        flow.Edges.Add(Edge(http, inspect));
        var execution = SeedExecution(flow);

        await new FlowExecutor(
            _flows,
            new RecordingTransport(),
            new StubCodeExecutionService(),
            _executions,
            new StubCrossReferenceRepository(),
            new FakeEmailSender(), NullLogger<FlowExecutor>.Instance).ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        var inspectStep = _executions.StepExecutions.Single(item => item.NodeName == "Inspect");
        Assert.Contains("text:helper-preludepostflight-code", inspectStep.ReceivedInput);
    }

    [Fact]
    public async Task ExecuteFlowAsync_RunsEveryNodeOfAValidDag()
    {
        var extract = Node("Extract");
        var transform = Node("Transform");
        var load = Node("Load");

        var flow = new IntegrationFlow { Nodes = { extract, transform, load } };
        flow.Edges.Add(Edge(extract, transform));
        flow.Edges.Add(Edge(transform, load));

        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(3, result.SuccessRecords);
        Assert.Equal(0, result.FailedRecords);
        Assert.Equal(new[] { "Extract", "Transform", "Load" }, _executions.StepExecutions.Select(s => s.NodeName));
    }

    [Fact]
    public async Task ExecuteFlowAsync_RunsNodesInTopologicalOrder()
    {
        var a = Node("A");
        var b = Node("B");
        var c = Node("C");
        var d = Node("D");

        var flow = new IntegrationFlow { Nodes = { d, c, b, a } };
        flow.Edges.Add(Edge(a, b));
        flow.Edges.Add(Edge(a, c));
        flow.Edges.Add(Edge(b, d));
        flow.Edges.Add(Edge(c, d));

        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        var order = _executions.StepExecutions.Select(s => s.NodeName).ToList();

        Assert.Equal(4, order.Count);
        Assert.Equal("A", order[0]);
        Assert.Equal("D", order[3]);
        Assert.True(order.IndexOf("B") < order.IndexOf("D"));
        Assert.True(order.IndexOf("C") < order.IndexOf("D"));
    }

    [Fact]
    public async Task ExecuteFlowFromNodeAsync_RunsSelectedNodeAndItsDownstreamWithoutRerunningEarlierNodes()
    {
        var earlier = Node("Earlier");
        var selected = Node("Selected");
        var downstream = Node("Downstream");
        var flow = new IntegrationFlow { Nodes = { earlier, selected, downstream } };
        flow.Edges.Add(Edge(earlier, selected));
        flow.Edges.Add(Edge(selected, downstream));
        var execution = SeedExecution(flow);
        _executions.StepExecutions.Add(new StepExecution
        {
            FlowExecutionId = execution.Id,
            StepId = earlier.Id,
            NodeName = earlier.NodeName,
            Status = ExecutionStatus.Success,
            ResponsePayload = "{\"previous\":true}",
            CompletedAt = DateTime.UtcNow
        });

        var result = await CreateExecutor().ExecuteFlowFromNodeAsync(
            flow.Id,
            execution.Id,
            selected.Id,
            "{\"Earlier\":{\"previous\":true},\"Selected\":{\"stale\":true},\"Downstream\":{\"stale\":true}}",
            "{}",
            selected.Id,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { "Earlier", "Selected", "Downstream" }, _executions.StepExecutions.Select(step => step.NodeName));
    }

    [Fact]
    public async Task ExecuteFlowFromNodeAsync_ClearsDownstreamOutputsBeforeRerun()
    {
        var earlier = Node("Earlier");
        var selected = Node("Selected");
        var downstream = Node("Downstream");
        var flow = new IntegrationFlow { Nodes = { earlier, selected, downstream } };
        flow.Edges.Add(Edge(earlier, selected));
        flow.Edges.Add(Edge(selected, downstream));
        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowFromNodeAsync(
            flow.Id,
            execution.Id,
            selected.Id,
            "{\"Earlier\":{\"previous\":true},\"Selected\":{\"stale\":true},\"Downstream\":{\"stale\":true}}",
            "{}",
            selected.Id,
            CancellationToken.None);

        // The Debug card reports the state it received; values for the selected node and
        // everything downstream must already be gone, while upstream state is preserved.
        var selectedRun = _executions.StepExecutions.Single(step => step.NodeName == "Selected");
        using var payload = JsonDocument.Parse(selectedRun.ResponsePayload);
        var flowState = payload.RootElement.GetProperty("flowState");

        Assert.True(flowState.GetProperty("Earlier").GetProperty("previous").GetBoolean());
        Assert.False(flowState.TryGetProperty("Selected", out _));
        Assert.False(flowState.TryGetProperty("Downstream", out _));
    }

    [Fact]
    public async Task ExecuteFlowAsync_FailsWithCycleDetected_WhenTheDagContainsACycle()
    {
        var start = Node("Start");
        var a = Node("A");
        var b = Node("B");

        var flow = new IntegrationFlow { Nodes = { start, a, b } };
        flow.Edges.Add(Edge(start, a));
        flow.Edges.Add(Edge(a, b));
        flow.Edges.Add(Edge(b, a));

        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("Cycle detected in the flow DAG.", result.ErrorMessage);
        Assert.NotNull(result.CompletedAt);
    }

    [Fact]
    public async Task ExecuteFlowAsync_FailsWhenNoStartNodeExists()
    {
        var a = Node("A");
        var b = Node("B");

        var flow = new IntegrationFlow { Nodes = { a, b } };
        flow.Edges.Add(Edge(a, b));
        flow.Edges.Add(Edge(b, a));

        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("Could not find a starting node.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteFlowAsync_FailsWhenFlowDoesNotExist()
    {
        var execution = new FlowExecution { FlowId = Guid.NewGuid(), TriggerSource = "Test" };
        _executions.Seed(execution);

        var result = await CreateExecutor().ExecuteFlowAsync(execution.FlowId, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("Flow not found.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteFlowAsync_SucceedsWithoutStepsWhenFlowHasNoNodes()
    {
        var flow = new IntegrationFlow();
        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Empty(_executions.StepExecutions);
    }

    [Fact]
    public async Task ExecuteFlowAsync_ThrowsWhenTheExecutionRecordIsMissing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateExecutor().ExecuteFlowAsync(Guid.NewGuid(), Guid.NewGuid(), null, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteFlowAsync_InjectsTriggerPayloadIntoFlowState()
    {
        var inspect = Node("Inspect");
        var flow = new IntegrationFlow { Nodes = { inspect } };
        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, "{\"orderId\":123}", CancellationToken.None);

        var step = Assert.Single(_executions.StepExecutions);
        Assert.Contains("\"orderId\": 123", step.ResponsePayload);
    }

    [Fact]
    public async Task ExecuteFlowAsync_CancelsWhenTheTokenIsAlreadyCancelled()
    {
        var flow = new IntegrationFlow { Nodes = { Node("A") } };
        var execution = SeedExecution(flow);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal("Execution was cancelled.", result.ErrorMessage);
    }
    [Fact]
    public async Task ExecuteFlowAsync_FanOutPersistsOneChildTransactionPerItemAndRunsDownstreamPerChild()
    {
        var source = Node("Source");
        var fanOut = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "FanOutOrders",
            StepType = StepType.FanOut,
            StepConfig = "{\"fanOut\":{\"arrayPath\":\"flowState.trigger.orders\",\"itemVariable\":\"order\",\"maxItems\":10}}"
        };
        var processItem = Node("ProcessOrder");
        var flow = new IntegrationFlow { Nodes = { source, fanOut, processItem } };
        flow.Edges.Add(Edge(source, fanOut));
        flow.Edges.Add(Edge(fanOut, processItem));
        var parent = SeedExecution(flow);

        var executor = CreateExecutor();
        var result = await executor.ExecuteFlowAsync(
            flow.Id, parent.Id, "{\"orders\":[{\"id\":1},{\"id\":2}]}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { "Source", "FanOutOrders" }, _executions.StepExecutions
            .Where(step => step.FlowExecutionId == parent.Id)
            .Select(step => step.NodeName));

        var children = (await _executions.GetFlowExecutionsAsync(new ExecutionQuery()))
            .Where(execution => execution.ParentExecutionId == parent.Id)
            .OrderBy(execution => execution.FanOutItemIndex)
            .ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.Equal(ExecutionStatus.Queued, child.Status));
        Assert.Equal(new[] { 1, 2 }, children.Select(child => child.FanOutItemIndex!.Value));
        Assert.All(children, child => Assert.Equal(2, child.FanOutItemCount));
        Assert.All(children, child => Assert.Equal(fanOut.Id, child.FanOutNodeId));
        Assert.All(children, child => Assert.Equal(new[] { processItem.Id }, JsonSerializer.Deserialize<Guid[]>(child.StartStepIdsJson!)));

        // The parent's state is stored once and shared; a child stores only its item.
        var snapshot = Assert.Single(_executions.FanOutSnapshots.Values);
        Assert.Equal(parent.Id, snapshot.ParentExecutionId);
        Assert.All(children, child => Assert.Equal(snapshot.Id, child.FanOutSnapshotId));
        Assert.All(children, child => Assert.Null(child.InitialFlowStateJson));
        Assert.Contains("\"orders\"", snapshot.FlowStateJson);

        var firstStateJson = await FanOutState.ResolveInitialFlowStateAsync(children[0], _executions);
        var firstState = JsonNode.Parse(firstStateJson!)!;
        Assert.Equal(1, firstState["order"]?["id"]?.GetValue<int>());
        Assert.Equal(1, firstState["FanOutOrders"]?["id"]?.GetValue<int>());
        Assert.Equal(2, firstState["trigger"]?["orders"]?.AsArray().Count);
        Assert.Equal("{\"id\":1}", children[0].FanOutItemJson);
        var secondState = JsonNode.Parse((await FanOutState.ResolveInitialFlowStateAsync(children[1], _executions))!)!;
        Assert.Equal(2, secondState["order"]?["id"]?.GetValue<int>());

        var firstChild = children[0];
        var childResult = await executor.ExecuteFlowFromNodesAsync(
            flow.Id,
            firstChild.Id,
            JsonSerializer.Deserialize<Guid[]>(firstChild.StartStepIdsJson!)!,
            firstStateJson!,
            firstChild.InitialPersistedStateJson!,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, childResult.Status);
        var childStep = Assert.Single(_executions.StepExecutions, step => step.FlowExecutionId == firstChild.Id);
        Assert.Equal("ProcessOrder", childStep.NodeName);
        Assert.Equal(1, JsonNode.Parse(childStep.ReceivedInput)?["id"]?.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteFlowAsync_FanOutRejectsMoreThanConfiguredLimitWithoutQueueingPartialChildren()
    {
        var fanOut = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "FanOutOrders",
            StepType = StepType.FanOut,
            StepConfig = "{\"fanOut\":{\"arrayPath\":\"flowState.trigger.orders\",\"itemVariable\":\"order\",\"maxItems\":1}}"
        };
        var flow = new IntegrationFlow { Nodes = { fanOut } };
        var parent = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(
            flow.Id, parent.Id, "{\"orders\":[{\"id\":1},{\"id\":2}]}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("exceeding its limit of 1", result.ErrorMessage);
        Assert.DoesNotContain((await _executions.GetFlowExecutionsAsync(new ExecutionQuery())), execution => execution.ParentExecutionId == parent.Id);
    }

}


