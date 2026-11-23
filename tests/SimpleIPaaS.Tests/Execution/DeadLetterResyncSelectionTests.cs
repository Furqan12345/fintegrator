using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

// A dead letter raised inside a ForEach body must only be resyncable from the loop card:
// re-running an inner card directly would skip the iteration state the executor builds.
public sealed class DeadLetterResyncSelectionTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private static IntegrationStep Node(string name, StepType stepType = StepType.Debug) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = stepType
    };

    private static IntegrationEdge Edge(IntegrationStep source, IntegrationStep target, string port = "") => new()
    {
        SourceNodeId = source.Id,
        TargetNodeId = target.Id,
        SourcePortId = port
    };

    private static DeadLetterController CreateController(
        StubExecutionRepository executions,
        StubIntegrationRepository flows)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();

        return new DeadLetterController(executions, flows, NullLogger<DeadLetterController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() }
            }
        };
    }

    private (IntegrationFlow Flow, FlowExecution Execution, DeadLetterEntry Entry, IntegrationStep Loop, IntegrationStep Inner)
        SeedLoopFailure()
    {
        var start = Node("Start");
        var loop = Node("Loop", StepType.ForEach);
        var inner = Node("Inner");
        var merge = Node("Merge");

        var flow = new IntegrationFlow { Name = "Loop flow" };
        flow.Nodes.Add(start);
        flow.Nodes.Add(loop);
        flow.Nodes.Add(inner);
        flow.Nodes.Add(merge);
        flow.Edges.Add(Edge(start, loop));
        flow.Edges.Add(Edge(loop, inner));
        flow.Edges.Add(Edge(loop, merge, "completed"));
        _flows.Seed(flow);

        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flow.Id,
            FlowName = flow.Name,
            Status = ExecutionStatus.Failed
        };
        _executions.Seed(execution);

        _executions.StepExecutions.Add(new StepExecution
        {
            FlowExecutionId = execution.Id,
            StepId = start.Id,
            NodeName = start.NodeName,
            Status = ExecutionStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-2)
        });
        _executions.StepExecutions.Add(new StepExecution
        {
            FlowExecutionId = execution.Id,
            StepId = loop.Id,
            NodeName = loop.NodeName,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        _executions.StepExecutions.Add(new StepExecution
        {
            FlowExecutionId = execution.Id,
            StepId = inner.Id,
            NodeName = inner.NodeName,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow
        });

        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowExecutionId = execution.Id,
            StepId = inner.Id,
            FlowName = flow.Name,
            NodeName = inner.NodeName,
            ErrorMessage = "boom",
            Status = "Pending",
            Payload = "{}",
            FlowStateJson = "{\"item\":{\"id\":1}}",
            PersistedStateJson = "{}"
        };
        _executions.DeadLetters.Add(entry);

        return (flow, execution, entry, loop, inner);
    }

    [Fact]
    public async Task ResyncNodes_ExcludeLoopBodyAndOfferTheOutermostLoop()
    {
        var (_, _, entry, loop, inner) = SeedLoopFailure();
        var controller = CreateController(_executions, _flows);

        var result = await controller.GetResyncNodes(entry.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var options = Assert.IsAssignableFrom<List<ResyncNodeOptionDto>>(ok.Value);
        Assert.DoesNotContain(options, option => option.StepId == inner.Id);

        var loopOption = Assert.Single(options, option => option.StepId == loop.Id);
        Assert.True(loopOption.ContainsFailedNode);
        Assert.True(loopOption.IsDefault);
    }

    [Fact]
    public async Task Retry_RejectsAnInnerLoopNode()
    {
        var (_, _, entry, _, inner) = SeedLoopFailure();
        var controller = CreateController(_executions, _flows);

        var result = await controller.Retry(entry.Id, new ResyncDeadLetterRequestDto
        {
            StartStepId = inner.Id,
            FlowStateJson = "{}"
        });

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Null(entry.ReplayRequestedAt);
    }

    [Fact]
    public async Task Retry_FromLoopCardPersistsTheEditedFlowStateAndStartNode()
    {
        var (_, _, entry, loop, _) = SeedLoopFailure();
        var controller = CreateController(_executions, _flows);
        const string editedState = "{\"item\":{\"id\":99},\"Start\":{\"fixed\":true}}";

        var result = await controller.Retry(entry.Id, new ResyncDeadLetterRequestDto
        {
            StartStepId = loop.Id,
            FlowStateJson = editedState
        });

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal("Retrying", entry.Status);
        Assert.Equal(loop.Id, entry.ReplayFromStepId);
        Assert.Equal(editedState, entry.FlowStateJson);
        Assert.NotNull(entry.ReplayRequestedAt);
    }

    [Fact]
    public async Task Retry_RejectsNonObjectFlowState()
    {
        var (_, _, entry, loop, _) = SeedLoopFailure();
        var controller = CreateController(_executions, _flows);

        var result = await controller.Retry(entry.Id, new ResyncDeadLetterRequestDto
        {
            StartStepId = loop.Id,
            FlowStateJson = "[1,2,3]"
        });

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Null(entry.ReplayRequestedAt);
        Assert.Equal("{\"item\":{\"id\":1}}", entry.FlowStateJson);
    }

    [Fact]
    public void NestedLoops_ExposeOnlyTheOutermostLoopForResync()
    {
        var outer = Node("Outer", StepType.ForEach);
        var innerLoop = Node("InnerLoop", StepType.ForEach);
        var leaf = Node("Leaf");
        var flow = new IntegrationFlow { Name = "Nested" };
        flow.Nodes.Add(outer);
        flow.Nodes.Add(innerLoop);
        flow.Nodes.Add(leaf);
        flow.Edges.Add(Edge(outer, innerLoop));
        flow.Edges.Add(Edge(innerLoop, leaf));

        var body = FlowGraphAnalysis.GetAllForEachBodyNodeIds(flow);

        Assert.Contains(innerLoop.Id, body);
        Assert.Contains(leaf.Id, body);
        Assert.DoesNotContain(outer.Id, body);
        Assert.Equal(outer.Id, FlowGraphAnalysis.GetOutermostForEachNodeId(flow, innerLoop.Id));
        Assert.Equal(outer.Id, FlowGraphAnalysis.GetOutermostForEachNodeId(flow, leaf.Id));
        Assert.Null(FlowGraphAnalysis.GetOutermostForEachNodeId(flow, outer.Id));
    }
}
