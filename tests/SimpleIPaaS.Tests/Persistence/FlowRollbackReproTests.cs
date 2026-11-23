using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

// Regression tests for the API-shaped rollback path: POST /{id}/publish â†’ PUT an edited graph
// (including one that archives a node that has execution history) â†’ publish again â†’ rollback.
// Before the fix this threw DbUpdateConcurrencyException and surfaced as HTTP 500.
public sealed class FlowRollbackReproTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public FlowRollbackReproTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(_connection).Options;
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options);

    private static IntegrationFlowDto Dto(Guid flowId, string name, params (Guid Id, string NodeName, string Url)[] nodes) => new()
    {
        Id = flowId,
        Name = name,
        TriggerType = "Manual",
        Status = "Draft",
        PersistedStateJson = "{}",
        Nodes = nodes.Select(node => new IntegrationStepDto
        {
            Id = node.Id,
            StepType = "HttpAction",
            NodeName = node.NodeName,
            EndpointUrl = node.Url,
            HttpMethod = "GET",
            UrlMode = "Manual"
        }).ToList(),
        Edges = new List<IntegrationEdgeDto>()
    };

    [Fact]
    public async Task ApiStyleRollbackRepro()
    {
        var flowId = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var nodeC = Guid.NewGuid();

        // POST /api/integrationflow
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var flow = Dto(flowId, "First", (nodeA, "Fetch", "https://a.test"), (nodeB, "Map", "https://b.test")).ToEntity();
            flow.Status = FlowStatus.Draft;
            flow.Id = flowId;
            await repository.AddAsync(flow);
        }

        // publish v1
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            await repository.PublishAsync(flowId, "v1");
        }

        // PUT changed graph (same node ids, changed urls)
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var flow = Dto(flowId, "Second", (nodeA, "Fetch", "https://a2.test"), (nodeB, "Map", "https://b2.test")).ToEntity();
            Assert.True(await repository.UpdateAsync(flow));
        }

        // publish v2
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            Assert.NotNull(await repository.PublishAsync(flowId, "v2"));
        }

        // PUT a third draft (adds a node)
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var flow = Dto(flowId, "Third", (nodeA, "Fetch", "https://a3.test"), (nodeB, "Map", "https://b3.test"), (nodeC, "New", "https://c.test")).ToEntity();
            Assert.True(await repository.UpdateAsync(flow));
        }

        // rollback to 1
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var restored = await repository.RollbackAsync(flowId, 1, "back to v1");
            Assert.NotNull(restored);
            Assert.Equal("First", restored!.Name);
            Assert.Equal(3, restored.PublishedVersion);
        }
    }

    [Fact]
    public async Task ApiStyleRollbackReproWithArchivedHistory()
    {
        var flowId = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var flow = Dto(flowId, "First", (nodeA, "Fetch", "https://a.test"), (nodeB, "Map", "https://b.test")).ToEntity();
            flow.Status = FlowStatus.Draft;
            flow.Id = flowId;
            await repository.AddAsync(flow);
            await repository.PublishAsync(flowId, "v1");
            context.FlowExecutions.Add(new FlowExecution { Id = executionId, FlowId = flowId, FlowName = "First" });
            context.StepExecutions.Add(new StepExecution { FlowExecutionId = executionId, StepId = nodeB, NodeName = "Map" });
            await context.SaveChangesAsync();
        }

        // remove node B (archives it because of history)
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var flow = Dto(flowId, "Second", (nodeA, "Fetch", "https://a2.test")).ToEntity();
            Assert.True(await repository.UpdateAsync(flow));
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var restored = await repository.RollbackAsync(flowId, 1, "back to v1");
            Assert.NotNull(restored);
        }
    }

    [Fact]
    public async Task ApiStyleRollbackReproWithEdges()
    {
        var flowId = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var edgeId = Guid.NewGuid();

        IntegrationFlowDto Build(string name) => new()
        {
            Id = flowId,
            Name = name,
            TriggerType = "Manual",
            Status = "Draft",
            PersistedStateJson = "{}",
            Nodes = new List<IntegrationStepDto>
            {
                new() { Id = nodeA, StepType = "HttpAction", NodeName = "Fetch", EndpointUrl = "https://a.test", HttpMethod = "GET", UrlMode = "Manual" },
                new() { Id = nodeB, StepType = "Mapping", NodeName = "Map", MappingCode = "return input;" }
            },
            Edges = new List<IntegrationEdgeDto>
            {
                new() { Id = edgeId, SourceNodeId = nodeA, TargetNodeId = nodeB }
            }
        };

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var flow = Build("First").ToEntity();
            flow.Status = FlowStatus.Draft;
            flow.Id = flowId;
            await repository.AddAsync(flow);
            await repository.PublishAsync(flowId, "v1");
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            Assert.True(await repository.UpdateAsync(Build("Second").ToEntity()));
            await repository.PublishAsync(flowId, "v2");
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context);
            var restored = await repository.RollbackAsync(flowId, 1, "back to v1");
            Assert.NotNull(restored);
        }
    }

    public void Dispose() => _connection.Dispose();
}



