using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class TenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public TenantIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(_connection)
            .Options;

        using var seedContext = CreateContext(TenantA);
        seedContext.Database.EnsureCreated();

        seedContext.IntegrationFlows.Add(new IntegrationFlow { TenantId = TenantA, Name = "Tenant A flow" });
        seedContext.IntegrationFlows.Add(new IntegrationFlow { TenantId = TenantB, Name = "Tenant B flow" });
        seedContext.Connections.Add(new Connection { TenantId = TenantA, Name = "A connection", AuthType = AuthType.Basic });
        seedContext.Connections.Add(new Connection { TenantId = TenantB, Name = "B connection", AuthType = AuthType.Bearer });
        seedContext.SaveChanges();
    }

    private IPaaSContext CreateContext(Guid tenantId) => new(_options, new StubTenantContext(tenantId));

    [Fact]
    public void QueryFilter_HidesFlowsBelongingToAnotherTenant()
    {
        using var context = CreateContext(TenantA);

        var flows = context.IntegrationFlows.ToList();

        Assert.Equal("Tenant A flow", Assert.Single(flows).Name);
    }

    [Fact]
    public void QueryFilter_ShowsEachTenantOnlyItsOwnConnections()
    {
        using var contextA = CreateContext(TenantA);
        using var contextB = CreateContext(TenantB);

        Assert.Equal("A connection", Assert.Single(contextA.Connections.ToList()).Name);
        Assert.Equal("B connection", Assert.Single(contextB.Connections.ToList()).Name);
    }

    [Fact]
    public void QueryFilter_PreventsFindingAnotherTenantsRowById()
    {
        Guid otherTenantFlowId;
        using (var contextB = CreateContext(TenantB))
        {
            otherTenantFlowId = contextB.IntegrationFlows.Single().Id;
        }

        using var contextA = CreateContext(TenantA);

        Assert.Null(contextA.IntegrationFlows.FirstOrDefault(f => f.Id == otherTenantFlowId));
    }

    [Fact]
    public void QueryFilter_ReturnsNothingForAnUnknownTenant()
    {
        using var context = CreateContext(Guid.NewGuid());

        Assert.Empty(context.IntegrationFlows.ToList());
        Assert.Empty(context.Connections.ToList());
    }

    [Fact]
    public void IgnoreQueryFilters_RevealsBothTenantsRows()
    {
        using var context = CreateContext(TenantA);

        Assert.Equal(2, context.IntegrationFlows.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void QueryFilter_AppliesToExecutionHistory()
    {
        Guid flowIdA;
        using (var seed = CreateContext(TenantA))
        {
            flowIdA = seed.IntegrationFlows.Single().Id;
            var flowIdB = seed.IntegrationFlows.IgnoreQueryFilters().Single(f => f.TenantId == TenantB).Id;

            seed.FlowExecutions.Add(new FlowExecution { TenantId = TenantA, FlowId = flowIdA, TriggerSource = "A" });
            seed.FlowExecutions.Add(new FlowExecution { TenantId = TenantB, FlowId = flowIdB, TriggerSource = "B" });
            seed.SaveChanges();
        }

        using var context = CreateContext(TenantA);

        var execution = Assert.Single(context.FlowExecutions.ToList());
        Assert.Equal("A", execution.TriggerSource);
        Assert.Equal(flowIdA, execution.FlowId);
    }

    [Fact]
    public void QueryFilter_AppliesToFlowNodes()
    {
        using (var seed = CreateContext(TenantA))
        {
            foreach (var flow in seed.IntegrationFlows.IgnoreQueryFilters().ToList())
            {
                seed.IntegrationSteps.Add(new IntegrationStep
                {
                    TenantId = flow.TenantId,
                    FlowId = flow.Id,
                    NodeName = flow.TenantId == TenantA ? "A node" : "B node",
                    StepType = StepType.Debug
                });
            }

            seed.SaveChanges();
        }

        using var context = CreateContext(TenantB);

        Assert.Equal("B node", Assert.Single(context.IntegrationSteps.ToList()).NodeName);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
