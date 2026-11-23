using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class IntegrationRepositoryIdCollisionTests
{

    [Fact]
    public async Task AddAsync_RegeneratesNodeAndEdgeIdsThatBelongToOtherFlows()
    {
        using var connection = new SqliteConnection("Data Source=FlowIdCollisionCreate;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var foreignNodeId = Guid.NewGuid();
        var foreignNode2Id = Guid.NewGuid();
        var foreignEdgeId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            var existingFlow = NewFlow("Existing flow");
            context.IntegrationFlows.Add(existingFlow);
            context.IntegrationSteps.Add(NewNode(existingFlow.Id, foreignNodeId, "Existing node"));
            context.IntegrationSteps.Add(NewNode(existingFlow.Id, foreignNode2Id, "Existing node 2"));
            context.IntegrationEdges.Add(new IntegrationEdge
            {
                Id = foreignEdgeId,
                FlowId = existingFlow.Id,
                SourceNodeId = foreignNodeId,
                TargetNodeId = foreignNode2Id
            });
            await context.SaveChangesAsync();
        }

        var copiedNodeId = foreignNodeId;
        var freshNodeId = Guid.NewGuid();
        var copiedEdgeId = foreignEdgeId;
        var flow = NewFlow("AI draft copied ids");
        flow.DefaultDeadLetterResyncStepId = copiedNodeId;
        flow.Nodes.Add(NewNode(flow.Id, copiedNodeId, "Copied node"));
        flow.Nodes.Add(NewNode(flow.Id, freshNodeId, "Fresh node"));
        flow.Edges.Add(new IntegrationEdge
        {
            Id = copiedEdgeId,
            FlowId = flow.Id,
            SourceNodeId = copiedNodeId,
            TargetNodeId = freshNodeId
        });

        using (var context = new IPaaSContext(options))
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(flow);
        }

        using (var context = new IPaaSContext(options))
        {
            var saved = context.IntegrationFlows
                .Include(item => item.Nodes)
                .Include(item => item.Edges)
                .Single(item => item.Name == "AI draft copied ids");

            var nodes = saved.Nodes.OrderBy(node => node.NodeName).ToList();
            Assert.Equal(2, nodes.Count);
            var remappedNode = Assert.Single(nodes, node => node.NodeName == "Copied node");
            var freshNode = Assert.Single(nodes, node => node.NodeName == "Fresh node");
            Assert.NotEqual(copiedNodeId, remappedNode.Id);
            Assert.Equal(freshNodeId, freshNode.Id);
            Assert.Equal(remappedNode.Id, saved.DefaultDeadLetterResyncStepId);

            var edge = Assert.Single(saved.Edges);
            Assert.NotEqual(copiedEdgeId, edge.Id);
            Assert.Equal(remappedNode.Id, edge.SourceNodeId);
            Assert.Equal(freshNodeId, edge.TargetNodeId);

            Assert.Single(context.IntegrationSteps, node => node.Id == foreignNodeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_RegeneratesForeignNodeIdsAddedToExistingFlow()
    {
        using var connection = new SqliteConnection("Data Source=FlowIdCollisionUpdate;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var foreignNodeId = Guid.NewGuid();
        var ownNodeId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            var otherFlow = NewFlow("Other flow");
            context.IntegrationFlows.Add(otherFlow);
            context.IntegrationSteps.Add(NewNode(otherFlow.Id, foreignNodeId, "Other flow node"));

            var flow = NewFlow("My flow");
            flow.Id = flowId;
            context.IntegrationFlows.Add(flow);
            context.IntegrationSteps.Add(NewNode(flowId, ownNodeId, "My node"));
            await context.SaveChangesAsync();
        }

        var updated = NewFlow("My flow");
        updated.Id = flowId;
        updated.Nodes.Add(NewNode(flowId, ownNodeId, "My node"));
        updated.Nodes.Add(NewNode(flowId, foreignNodeId, "Copied node"));

        using (var context = new IPaaSContext(options))
        {
            var repository = new IntegrationRepository(context);
            Assert.True(await repository.UpdateAsync(updated));
        }

        using (var context = new IPaaSContext(options))
        {
            var savedNodes = context.IntegrationSteps.AsNoTracking().Where(node => node.FlowId == flowId).ToList();
            Assert.Equal(2, savedNodes.Count);
            Assert.Single(savedNodes, node => node.Id == ownNodeId);
            var copied = Assert.Single(savedNodes, node => node.NodeName == "Copied node");
            Assert.NotEqual(foreignNodeId, copied.Id);
            Assert.Single(context.IntegrationSteps, node => node.Id == foreignNodeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_RegeneratesIdsMatchingArchivedNodesOfSameFlow()
    {
        using var connection = new SqliteConnection("Data Source=FlowIdCollisionArchived;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var archivedNodeId = Guid.NewGuid();
        var ownNodeId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            var flow = NewFlow("My flow");
            flow.Id = flowId;
            context.IntegrationFlows.Add(flow);
            context.IntegrationSteps.Add(NewNode(flowId, ownNodeId, "My node"));
            var archived = NewNode(flowId, archivedNodeId, "Archived node");
            archived.IsArchived = true;
            context.IntegrationSteps.Add(archived);
            await context.SaveChangesAsync();
        }

        var updated = NewFlow("My flow");
        updated.Id = flowId;
        updated.Nodes.Add(NewNode(flowId, ownNodeId, "My node"));
        updated.Nodes.Add(NewNode(flowId, archivedNodeId, "Reused archived id"));

        using (var context = new IPaaSContext(options))
        {
            var repository = new IntegrationRepository(context);
            Assert.True(await repository.UpdateAsync(updated));
        }

        using (var context = new IPaaSContext(options))
        {
            var active = context.IntegrationSteps.AsNoTracking()
                .Where(node => node.FlowId == flowId && !node.IsArchived)
                .ToList();
            Assert.Equal(2, active.Count);
            Assert.Single(active, node => node.Id == ownNodeId);
            var reused = Assert.Single(active, node => node.NodeName == "Reused archived id");
            Assert.NotEqual(archivedNodeId, reused.Id);
        }
    }

    private static IntegrationFlow NewFlow(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        TriggerType = TriggerType.Manual,
        Status = FlowStatus.Draft
    };

    private static IntegrationStep NewNode(Guid flowId, Guid nodeId, string name) => new()
    {
        Id = nodeId,
        FlowId = flowId,
        NodeName = name,
        StepType = StepType.Schedule
    };
}



