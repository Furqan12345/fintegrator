using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class FlowVersionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public FlowVersionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options);

    [Fact]
    public async Task PublishCreatesImmutableVersionsAndEditingActiveFlowReturnsToDraft()
    {
        var flowId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                Name = "Version one",
                Nodes =
                {
                    new IntegrationStep
                    {
                        Id = Guid.NewGuid(),
                        FlowId = flowId,
                        StepType = StepType.Mapping,
                        NodeName = "Map",
                        EndpointUrl = "https://v1.example.test"
                    }
                }
            });
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var first = await repository.PublishAsync(flowId, "Initial release");

            Assert.NotNull(first);
            Assert.Equal(1, first!.VersionNumber);
            Assert.Equal("Initial release", first.ChangeNote);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var draft = await repository.GetByIdAsync(flowId);
            Assert.NotNull(draft);
            draft!.Name = "Version two";
            draft.Nodes.Single().EndpointUrl = "https://v2.example.test";

            Assert.True(await repository.UpdateAsync(draft));
            Assert.Equal(FlowStatus.Draft, (await repository.GetByIdAsync(flowId))!.Status);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var second = await repository.PublishAsync(flowId, "Second release");

            Assert.NotNull(second);
            Assert.Equal(2, second!.VersionNumber);
            Assert.Equal(2, (await repository.GetVersionsAsync(flowId)).Count);
        }
    }

    [Fact]
    public async Task UpdateArchivesRemovedNodesWithExecutionHistoryAndKeepsThemOutOfCurrentFlow()
    {
        var flowId = Guid.NewGuid();
        var archivedNodeId = Guid.NewGuid();
        var retainedNodeId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                Name = "Shopify orders",
                Nodes =
                {
                    new IntegrationStep { Id = archivedNodeId, FlowId = flowId, StepType = StepType.HttpAction, NodeName = "Old request" },
                    new IntegrationStep { Id = retainedNodeId, FlowId = flowId, StepType = StepType.CrossReferenceStore, NodeName = "Store orders" }
                }
            });
            context.FlowExecutions.Add(new FlowExecution { Id = executionId, FlowId = flowId, FlowName = "Shopify orders" });
            context.StepExecutions.Add(new StepExecution { FlowExecutionId = executionId, StepId = archivedNodeId, NodeName = "Old request" });
            await context.SaveChangesAsync();
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var draft = await repository.GetByIdAsync(flowId);
            Assert.NotNull(draft);
            context.ChangeTracker.Clear();
            draft!.Nodes.Remove(draft.Nodes.Single(node => node.Id == archivedNodeId));

            Assert.True(await repository.UpdateAsync(draft));
            var currentFlow = await repository.GetByIdAsync(flowId);
            Assert.NotNull(currentFlow);
            Assert.Equal(retainedNodeId, currentFlow!.ToDto().Nodes.Single().Id);
            Assert.True(await context.IntegrationSteps.IgnoreQueryFilters().AnyAsync(node => node.Id == archivedNodeId && node.IsArchived));
            Assert.True(await context.StepExecutions.AnyAsync(step => step.StepId == archivedNodeId));
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var draft = await repository.GetByIdAsync(flowId);
            Assert.NotNull(draft);
            draft!.Description = "Saved again";
            Assert.True(await repository.UpdateAsync(draft));
            Assert.Equal("Saved again", (await repository.GetByIdAsync(flowId))!.Description);
        }
    }

    [Fact]
    public async Task RollbackRestoresTargetSnapshotAndAppendsANewVersion()
    {
        var flowId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                Name = "Original",
                HelperCode = "static class FlowHelpers { public static string Version() => \"original\"; }",
                Nodes =
                {
                    new IntegrationStep
                    {
                        Id = Guid.NewGuid(),
                        FlowId = flowId,
                        StepType = StepType.Mapping,
                        NodeName = "Map",
                        EndpointUrl = "https://original.example.test"
                    }
                }
            });
            await repository.PublishAsync(flowId);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var draft = await repository.GetByIdAsync(flowId);
            draft!.Name = "Changed";
            draft.HelperCode = "static class FlowHelpers { public static string Version() => \"changed\"; }";
            draft.Nodes.Single().EndpointUrl = "https://changed.example.test";
            await repository.UpdateAsync(draft);
            Assert.Equal("static class FlowHelpers { public static string Version() => \"changed\"; }", (await repository.GetByIdAsync(flowId))!.HelperCode);
            await repository.PublishAsync(flowId);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var restored = await repository.RollbackAsync(flowId, 1);

            Assert.NotNull(restored);
            Assert.Equal("Original", restored!.Name);
            Assert.Equal("static class FlowHelpers { public static string Version() => \"original\"; }", restored.HelperCode);
            Assert.Equal("https://original.example.test", restored.Nodes.Single().EndpointUrl);
            Assert.Equal(3, restored.PublishedVersion);
            Assert.Equal(FlowStatus.Active, restored.Status);

            var versions = await repository.GetVersionsAsync(flowId);
            Assert.Equal(new[] { 3, 2, 1 }, versions.Select(version => version.VersionNumber).ToArray());
            Assert.Equal(1, versions[0].RolledBackFromVersion);
        }
    }

    // The reported bug: after a rollback the next Publish appended another "Published from Flow
    // Designer" version even though the draft was identical to the active snapshot. Publishing an
    // unchanged flow is a no-op now, so a repeated click or a second surface cannot mint a version.
    [Fact]
    public async Task PublishingAnUnchangedFlowDoesNotCreateAnotherVersion()
    {
        var flowId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                Name = "Idempotent publish",
                Nodes =
                {
                    new IntegrationStep
                    {
                        Id = Guid.NewGuid(),
                        FlowId = flowId,
                        StepType = StepType.Mapping,
                        NodeName = "Map",
                        EndpointUrl = "https://same.example.test"
                    }
                }
            });
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var first = await repository.PublishAsync(flowId, "Published from Flow Designer");
            Assert.Equal(1, first!.VersionNumber);

            // A second publish with nothing changed must return the existing version, not append one.
            var second = await repository.PublishAsync(flowId, "Published from Flow Designer");
            Assert.Equal(1, second!.VersionNumber);
            Assert.Single(await repository.GetVersionsAsync(flowId));
        }
    }

    [Fact]
    public async Task PublishingAfterRollbackDoesNotAppendAPhantomVersion()
    {
        var flowId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                Name = "Rollback then publish",
                Nodes =
                {
                    new IntegrationStep { Id = Guid.NewGuid(), FlowId = flowId, StepType = StepType.Mapping, NodeName = "Map", EndpointUrl = "https://v1.example.test" }
                }
            });
            await repository.PublishAsync(flowId, "v1");
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var draft = await repository.GetByIdAsync(flowId);
            draft!.Nodes.Single().EndpointUrl = "https://v2.example.test";
            await repository.UpdateAsync(draft);
            await repository.PublishAsync(flowId, "v2");
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var restored = await repository.RollbackAsync(flowId, 1);
            Assert.Equal(3, restored!.PublishedVersion);

            // Republishing right after a rollback restores an identical snapshot: no new version.
            var republished = await repository.PublishAsync(flowId, "Published from Flow Designer");
            Assert.Equal(3, republished!.VersionNumber);
            Assert.Equal(3, (await repository.GetVersionsAsync(flowId)).Count);
        }
    }

    // The root cause behind the phantom version: the designer rebuilt every edge with a fresh Guid
    // on each save, so an untouched flow looked modified and the edge rows churned. Saving the same
    // graph twice must keep edge identity and publish must then stay at one version.
    [Fact]
    public async Task SavingTheSameGraphTwiceKeepsEdgeIdentityAndPublishesOnce()
    {
        var flowId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                Name = "Stable edges",
                Nodes =
                {
                    new IntegrationStep { Id = sourceId, FlowId = flowId, StepType = StepType.Mapping, NodeName = "Source" },
                    new IntegrationStep { Id = targetId, FlowId = flowId, StepType = StepType.Mapping, NodeName = "Target" }
                },
                Edges =
                {
                    new IntegrationEdge { Id = edgeId, FlowId = flowId, SourceNodeId = sourceId, TargetNodeId = targetId, SourcePortId = "right", TargetPortId = "left" }
                }
            });
            await repository.PublishAsync(flowId, "v1");
        }

        // Re-save the exact same graph, the way an autosave or a repeat Save would.
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var reloaded = await repository.GetByIdAsync(flowId);
            Assert.Equal(edgeId, reloaded!.Edges.Single().Id);
            Assert.True(await repository.UpdateAsync(reloaded));
            Assert.Equal(edgeId, (await repository.GetByIdAsync(flowId))!.Edges.Single().Id);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var republished = await repository.PublishAsync(flowId, "Published from Flow Designer");
            Assert.Equal(1, republished!.VersionNumber);
            Assert.Single(await repository.GetVersionsAsync(flowId));
        }
    }

    [Fact]
    public async Task PublishingARealChangeStillCreatesAVersion()
    {
        var flowId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                Name = "Real change",
                Nodes =
                {
                    new IntegrationStep { Id = Guid.NewGuid(), FlowId = flowId, StepType = StepType.Mapping, NodeName = "Map", EndpointUrl = "https://one.example.test" }
                }
            });
            await repository.PublishAsync(flowId, "v1");
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var draft = await repository.GetByIdAsync(flowId);
            draft!.Nodes.Single().EndpointUrl = "https://two.example.test";
            await repository.UpdateAsync(draft);

            var second = await repository.PublishAsync(flowId, "v2");
            Assert.Equal(2, second!.VersionNumber);
        }
    }

    public void Dispose() => _connection.Dispose();
}


