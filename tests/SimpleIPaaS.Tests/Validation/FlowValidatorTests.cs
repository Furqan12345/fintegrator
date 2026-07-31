using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Tests.Validation;

public class FlowValidatorTests
{
    private static IntegrationStepDto Node(string name, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        NodeName = name,
        StepType = "Mapping"
    };

    private static IntegrationEdgeDto Edge(Guid source, Guid target) => new()
    {
        Id = Guid.NewGuid(),
        SourceNodeId = source,
        TargetNodeId = target
    };

    [Fact]
    public void Validate_AcceptsValidDag()
    {
        var first = Node("Fetch");
        var second = Node("Transform");
        var third = Node("Push");

        var flow = new IntegrationFlowDto
        {
            Nodes = { first, second, third },
            Edges = { Edge(first.Id, second.Id), Edge(second.Id, third.Id), Edge(first.Id, third.Id) }
        };

        Assert.Empty(FlowValidator.Validate(flow));
    }

    [Fact]
    public void Validate_RejectsDuplicateNodeNames()
    {
        var flow = new IntegrationFlowDto { Nodes = { Node("Fetch"), Node("Fetch") } };

        var errors = FlowValidator.Validate(flow);

        Assert.Single(errors);
        Assert.Contains("'Fetch' is used more than once", errors[0]);
    }

    [Fact]
    public void Validate_TreatsNodeNamesAsCaseInsensitiveAndTrimmed()
    {
        var flow = new IntegrationFlowDto { Nodes = { Node("Fetch"), Node("  fetch ") } };

        Assert.Single(FlowValidator.Validate(flow));
    }

    [Fact]
    public void Validate_IgnoresBlankNodeNamesWhenCheckingForDuplicates()
    {
        var flow = new IntegrationFlowDto { Nodes = { Node(string.Empty), Node("   ") } };

        Assert.Empty(FlowValidator.Validate(flow));
    }

    [Fact]
    public void Validate_RejectsEdgeWithMissingSourceNode()
    {
        var target = Node("Target");
        var edge = Edge(Guid.NewGuid(), target.Id);
        var flow = new IntegrationFlowDto { Nodes = { target }, Edges = { edge } };

        var errors = FlowValidator.Validate(flow);

        Assert.Single(errors);
        Assert.Contains($"Edge {edge.Id} references a source node that does not exist.", errors);
    }

    [Fact]
    public void Validate_RejectsEdgeWithMissingTargetNode()
    {
        var source = Node("Source");
        var edge = Edge(source.Id, Guid.NewGuid());
        var flow = new IntegrationFlowDto { Nodes = { source }, Edges = { edge } };

        var errors = FlowValidator.Validate(flow);

        Assert.Single(errors);
        Assert.Contains($"Edge {edge.Id} references a target node that does not exist.", errors);
    }

    [Fact]
    public void Validate_RejectsEdgeWithBothEndpointsMissing()
    {
        var flow = new IntegrationFlowDto
        {
            Nodes = { Node("Lonely") },
            Edges = { Edge(Guid.NewGuid(), Guid.NewGuid()) }
        };

        Assert.Equal(2, FlowValidator.Validate(flow).Count);
    }

    [Fact]
    public void Validate_RejectsSelfReferencingNode()
    {
        var node = Node("Loop");
        var flow = new IntegrationFlowDto { Nodes = { node }, Edges = { Edge(node.Id, node.Id) } };

        var errors = FlowValidator.Validate(flow);

        Assert.Single(errors);
        Assert.Contains("cycle", errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsCycle()
    {
        var a = Node("A");
        var b = Node("B");
        var c = Node("C");

        var flow = new IntegrationFlowDto
        {
            Nodes = { a, b, c },
            Edges = { Edge(a.Id, b.Id), Edge(b.Id, c.Id), Edge(c.Id, a.Id) }
        };

        var errors = FlowValidator.Validate(flow);

        Assert.Single(errors);
        Assert.Equal("The flow contains a cycle. Flows must be acyclic.", errors[0]);
    }

    [Fact]
    public void Validate_RejectsCycleReachableFromAStartNode()
    {
        var start = Node("Start");
        var a = Node("A");
        var b = Node("B");

        var flow = new IntegrationFlowDto
        {
            Nodes = { start, a, b },
            Edges = { Edge(start.Id, a.Id), Edge(a.Id, b.Id), Edge(b.Id, a.Id) }
        };

        var errors = FlowValidator.Validate(flow);

        Assert.Single(errors);
        Assert.Equal("The flow contains a cycle. Flows must be acyclic.", errors[0]);
    }

    [Fact]
    public void Validate_AcceptsFlowWithNoNodes()
    {
        Assert.Empty(FlowValidator.Validate(new IntegrationFlowDto()));
    }

    [Fact]
    public void Validate_AcceptsDisconnectedButAcyclicComponents()
    {
        var a = Node("A");
        var b = Node("B");
        var c = Node("C");
        var d = Node("D");

        var flow = new IntegrationFlowDto
        {
            Nodes = { a, b, c, d },
            Edges = { Edge(a.Id, b.Id), Edge(c.Id, d.Id) }
        };

        Assert.Empty(FlowValidator.Validate(flow));
    }

    [Fact]
    public void Validate_ReportsDuplicateNodeIds()
    {
        var sharedId = Guid.NewGuid();
        var flow = new IntegrationFlowDto { Nodes = { Node("A", sharedId), Node("B", sharedId) } };

        var errors = FlowValidator.Validate(flow);

        Assert.Contains(errors, error => error.Contains("used more than once", StringComparison.OrdinalIgnoreCase));
    }
}
