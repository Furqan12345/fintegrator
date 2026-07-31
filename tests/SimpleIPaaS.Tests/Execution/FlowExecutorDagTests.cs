using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public class FlowExecutorDagTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private FlowExecutor CreateExecutor() => new(
        _flows,
        new UnreachableTransportEngine(),
        new UnreachableCodeExecutionService(),
        _executions,
        new StubCrossReferenceRepository(),
        NullLogger<FlowExecutor>.Instance);

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
        var execution = new FlowExecution { FlowId = flow.Id, TenantId = flow.TenantId, TriggerSource = "Test" };
        _executions.Seed(execution);
        return execution;
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
}
