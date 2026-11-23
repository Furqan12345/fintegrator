using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Tests.Performance;

// List pages (Integrations, Dashboard, Dead Letters, Logs, Packet Logs, Transactions) only count a flow's steps and
// look for Schedule steps. The summary query must give them exactly that and none of the scripts or edges.
public sealed class IntegrationSummaryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<IPaaSContext> _options;

    public IntegrationSummaryTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(_connection).Options;
        using var context = new IPaaSContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<Guid> SeedAsync()
    {
        var script = new string('s', 50_000);
        using var context = new IPaaSContext(_options);
        var integration = new Integration { Name = "Orders" };
        var older = new IntegrationFlow { Name = "Older flow", IntegrationId = integration.Id, UpdatedAt = DateTime.UtcNow.AddDays(-2), HelperCode = script, PersistedStateJson = "{\"big\":\"" + script + "\"}" };
        var newer = new IntegrationFlow { Name = "Newer flow", IntegrationId = integration.Id, UpdatedAt = DateTime.UtcNow, Status = FlowStatus.Active, TriggerType = TriggerType.Cron, CronExpression = "0 * * * *" };

        IntegrationStep Step(IntegrationFlow flow, string name, StepType type, bool archived = false) => new()
        {
            FlowId = flow.Id, NodeName = name, StepType = type, IsArchived = archived,
            MappingCode = script, PreFlightCode = script, PostFlightCode = script, StepConfig = "{\"x\":\"" + script + "\"}"
        };

        newer.Nodes.Add(Step(newer, "Schedule", StepType.Schedule));
        newer.Nodes.Add(Step(newer, "Fetch", StepType.HttpAction));
        newer.Nodes.Add(Step(newer, "Old fetch", StepType.HttpAction, archived: true));
        older.Nodes.Add(Step(older, "Map", StepType.Mapping));
        integration.Flows.Add(older);
        integration.Flows.Add(newer);
        context.Integrations.Add(integration);
        await context.SaveChangesAsync();
        return integration.Id;
    }

    [Fact]
    public async Task Summaries_CarryCountsTypesAndOrder_ButNoScriptsOrEdges()
    {
        await SeedAsync();
        using var context = new IPaaSContext(_options);
        var repository = new IntegrationCatalogRepository(context);

        var summary = Assert.Single(await repository.GetAllSummariesAsync());
        Assert.Equal("Orders", summary.Name);
        Assert.Equal(new[] { "Newer flow", "Older flow" }, summary.Flows.Select(flow => flow.Name).ToArray());

        var newer = summary.Flows.First();
        Assert.Equal(FlowStatus.Active, newer.Status);
        Assert.Equal(TriggerType.Cron, newer.TriggerType);
        Assert.Equal("0 * * * *", newer.CronExpression);
        Assert.Equal(2, newer.Nodes.Count);   // the archived step is not counted
        Assert.Contains(newer.Nodes, node => node.StepType == StepType.Schedule);
        Assert.All(summary.Flows.SelectMany(flow => flow.Nodes), node =>
        {
            Assert.NotEqual(string.Empty, node.NodeName);
            Assert.Equal(string.Empty, node.MappingCode);
            Assert.Equal(string.Empty, node.PreFlightCode);
            Assert.Equal(string.Empty, node.PostFlightCode);
            Assert.Equal(string.Empty, node.StepConfig);
        });
        Assert.All(summary.Flows, flow =>
        {
            Assert.Empty(flow.Edges);
            Assert.Equal(string.Empty, flow.HelperCode);
            Assert.Equal("{}", flow.PersistedStateJson);
        });
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Summaries_MatchTheFullListOnWhatThePagesUse_AndAreFarSmallerOnTheWire()
    {
        await SeedAsync();
        using var context = new IPaaSContext(_options);
        var repository = new IntegrationCatalogRepository(context);

        var full = (await repository.GetAllAsync()).Select(item => item.ToDto()).ToList();
        context.ChangeTracker.Clear();
        var lite = (await repository.GetAllSummariesAsync()).Select(item => item.ToDto()).ToList();

        Assert.Equal(full.Count, lite.Count);
        Assert.Equal(full[0].Flows.Select(f => f.Id), lite[0].Flows.Select(f => f.Id));
        Assert.Equal(full[0].Flows.Select(f => f.Nodes.Count), lite[0].Flows.Select(f => f.Nodes.Count));
        Assert.Equal(
            full[0].Flows.SelectMany(f => f.Nodes).Count(n => n.StepType == "Schedule"),
            lite[0].Flows.SelectMany(f => f.Nodes).Count(n => n.StepType == "Schedule"));

        var fullBytes = JsonSerializer.Serialize(full).Length;
        var liteBytes = JsonSerializer.Serialize(lite).Length;
        Assert.True(liteBytes * 20 < fullBytes, $"lite {liteBytes} bytes vs full {fullBytes} bytes");
    }

    [Fact]
    public async Task FlowSummaries_AreLight_OrderedByRecency_AndKeepOnlyActiveStepIdentity()
    {
        await SeedAsync();
        using var context = new IPaaSContext(_options);
        var repository = new IntegrationRepository(context);

        var summaries = (await repository.GetAllSummariesAsync()).ToList();
        Assert.Equal(new[] { "Newer flow", "Older flow" }, summaries.Select(flow => flow.Name).ToArray());
        Assert.Equal(2, summaries[0].Nodes.Count);                // archived step excluded, like the full list
        Assert.Contains(summaries[0].Nodes, node => node.StepType == StepType.Schedule);
        Assert.All(summaries, flow =>
        {
            Assert.Empty(flow.Edges);
            Assert.Equal(string.Empty, flow.HelperCode);
            Assert.Equal("{}", flow.PersistedStateJson);
            Assert.All(flow.Nodes, node => Assert.Equal(string.Empty, node.MappingCode));
        });
        Assert.Empty(context.ChangeTracker.Entries());

        context.ChangeTracker.Clear();
        var full = (await repository.GetAllAsync()).ToList();
        Assert.Equal(full.Select(f => f.Id), summaries.Select(f => f.Id));
        Assert.Equal(full.Select(f => f.Nodes.Count), summaries.Select(f => f.Nodes.Count));
    }
}
