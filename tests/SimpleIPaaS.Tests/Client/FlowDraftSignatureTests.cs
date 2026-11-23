using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Tests.Client;

public sealed class FlowDraftSignatureTests
{
    [Fact]
    public void IgnoreNodeAndEdgeOrderingIdsPositionsAndTimestamps()
    {
        var firstNode = new IntegrationStepDto
        {
            Id = Guid.NewGuid(), StepType = "Mapping", NodeName = "Map", MappingCode = "return \"{}\";",
            PositionX = 10, PositionY = 20
        };
        var secondNode = new IntegrationStepDto
        {
            Id = Guid.NewGuid(), StepType = "HTTPAction", NodeName = "Fetch", EndpointUrl = "https://example.test",
            PositionX = 300, PositionY = 20
        };
        var edge = new IntegrationEdgeDto
        {
            Id = Guid.NewGuid(), SourceNodeId = firstNode.Id, TargetNodeId = secondNode.Id,
            SourcePortId = "right", TargetPortId = "left"
        };

        var first = new IntegrationFlowDto
        {
            Id = Guid.NewGuid(),
            Name = "Flow",
            UpdatedAt = DateTime.UtcNow,
            Nodes = new List<IntegrationStepDto> { firstNode, secondNode },
            Edges = new List<IntegrationEdgeDto> { edge }
        };
        var second = new IntegrationFlowDto
        {
            Id = first.Id,
            Name = "Flow",
            UpdatedAt = DateTime.UtcNow.AddMinutes(5),
            Nodes = new List<IntegrationStepDto>
            {
                new()
                {
                    Id = secondNode.Id, StepType = "HTTPAction", NodeName = "Fetch",
                    EndpointUrl = "https://example.test", PositionX = 999, PositionY = 0
                },
                new()
                {
                    Id = firstNode.Id, StepType = "Mapping", NodeName = "Map",
                    MappingCode = "return \"{}\";", PositionX = -5, PositionY = 999
                }
            },
            Edges = new List<IntegrationEdgeDto>
            {
                new()
                {
                    Id = Guid.NewGuid(), SourceNodeId = firstNode.Id, TargetNodeId = secondNode.Id,
                    SourcePortId = "right", TargetPortId = "left"
                }
            }
        };

        Assert.Equal(FlowDraftSignature.Create(first), FlowDraftSignature.Create(second));
    }

    [Fact]
    public void DetectRealSemanticChanges()
    {
        var node = new IntegrationStepDto
        {
            Id = Guid.NewGuid(), StepType = "Mapping", NodeName = "Map", MappingCode = "return 1;"
        };
        var before = new IntegrationFlowDto { Name = "Flow", Nodes = new List<IntegrationStepDto> { node } };
        var after = new IntegrationFlowDto
        {
            Name = "Flow",
            Nodes = new List<IntegrationStepDto>
            {
                new()
                {
                    Id = node.Id, StepType = "Mapping", NodeName = "Map", MappingCode = "return 2;"
                }
            }
        };

        Assert.NotEqual(FlowDraftSignature.Create(before), FlowDraftSignature.Create(after));
    }
}
