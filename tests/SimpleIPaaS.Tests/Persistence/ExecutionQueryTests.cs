using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class ExecutionQueryTests
{

    private static async Task<(ExecutionRepository Repository, SqliteConnection Connection)> SeedAsync()
    {
        var connection = new SqliteConnection("Data Source=ExecutionQuery;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;
        var flowId = Guid.NewGuid();

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = "Orders sync" });
            context.FlowExecutions.AddRange(
                new FlowExecution
                {
                    FlowId = flowId,
                    FlowName = "Orders sync",
                    Status = ExecutionStatus.Success,
                    TriggerSource = "Manual",
                    StartedAt = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc),
                    Identifier = "ORD-1001",
                    Message = "Order 1001 stored successfully"
                },
                new FlowExecution
                {
                    FlowId = flowId,
                    FlowName = "Orders sync",
                    Status = ExecutionStatus.Failed,
                    TriggerSource = "Manual",
                    StartedAt = new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc),
                    Identifier = "ORD-1002",
                    Message = "Shopify rejected the order",
                    ErrorMessage = "HTTP request failed with status code 422"
                },
                new FlowExecution
                {
                    FlowId = flowId,
                    FlowName = "Inventory push",
                    Status = ExecutionStatus.Success,
                    TriggerSource = "Cron",
                    StartedAt = new DateTime(2026, 10, 14, 7, 15, 0, DateTimeKind.Utc),
                    Identifier = "SKU-77",
                    Message = "Inventory synced"
                });
            context.SaveChanges();
        }

        return (new ExecutionRepository(new IPaaSContext(options)), connection);
    }

    [Fact]
    public async Task Search_MatchesIdentifierMessageFlowNameAndError()
    {
        var (repository, connection) = await SeedAsync();
        using var _ = connection;

        var byIdentifier = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { Search = "ORD-1002" })).ToList();
        Assert.Single(byIdentifier);
        Assert.Equal("ORD-1002", byIdentifier[0].Identifier);

        var byMessage = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { Search = "stored successfully" })).ToList();
        Assert.Single(byMessage);
        Assert.Equal("ORD-1001", byMessage[0].Identifier);

        var byFlowName = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { Search = "inventory push" })).ToList();
        Assert.Single(byFlowName);
        Assert.Equal("Inventory push", byFlowName[0].FlowName);

        var byError = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { Search = "422" })).ToList();
        Assert.Single(byError);
        Assert.Equal("ORD-1002", byError[0].Identifier);
    }

    [Fact]
    public async Task Search_ByFullExecutionId_ReturnsThatExecutionOnly()
    {
        var (repository, connection) = await SeedAsync();
        using var _ = connection;

        var all = (await repository.GetFlowExecutionsAsync(new ExecutionQuery())).ToList();
        var target = all.Single(e => e.Identifier == "SKU-77");

        var result = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { Search = target.Id.ToString() })).ToList();

        Assert.Single(result);
        Assert.Equal(target.Id, result[0].Id);
    }

    [Fact]
    public async Task DateRange_IsInclusiveFromAndExclusiveUntil()
    {
        var (repository, connection) = await SeedAsync();
        using var _ = connection;

        var window = (await repository.GetFlowExecutionsAsync(new ExecutionQuery
        {
            SinceUtc = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc),
            UntilUtc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc)
        })).ToList();

        Assert.Single(window);
        Assert.Equal("ORD-1002", window[0].Identifier);
    }

    [Fact]
    public async Task IntegrationFilter_IncludesRunsRecordedBeforeAFlowWasMovedIntoIt()
    {
        // A run stores the integration it happened under. When a flow later moves into another
        // integration, the integration that owns the flow must still show that flow's runs,
        // otherwise the list page and the transactions deep link disagree.
        var connection = new SqliteConnection("Data Source=ExecutionIntegrationScope;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        using var _ = connection;
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var oldHome = Guid.NewGuid();
        var newHome = Guid.NewGuid();
        var movedFlow = Guid.NewGuid();
        var otherFlow = Guid.NewGuid();

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.Integrations.AddRange(
                new Integration { Id = oldHome, Name = "Old home" },
                new Integration { Id = newHome, Name = "New home" });
            context.IntegrationFlows.AddRange(
                new IntegrationFlow { Id = movedFlow, Name = "Moved flow", IntegrationId = newHome },
                new IntegrationFlow { Id = otherFlow, Name = "Other flow", IntegrationId = oldHome });
            context.FlowExecutions.AddRange(
                new FlowExecution
                {
                    FlowId = movedFlow,
                    IntegrationId = oldHome,
                    FlowName = "Moved flow",
                    Status = ExecutionStatus.Success,
                    StartedAt = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc),
                    Identifier = "MOVED-1"
                },
                new FlowExecution
                {
                    FlowId = otherFlow,
                    IntegrationId = oldHome,
                    FlowName = "Other flow",
                    Status = ExecutionStatus.Success,
                    StartedAt = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
                    Identifier = "OTHER-1"
                });
            context.SaveChanges();
        }

        var repository = new ExecutionRepository(new IPaaSContext(options));

        var currentHome = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { IntegrationId = newHome })).ToList();
        Assert.Single(currentHome);
        Assert.Equal("MOVED-1", currentHome[0].Identifier);

        // The run follows the flow: the previous home keeps only the runs of the flows it still has.
        var previousHome = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { IntegrationId = oldHome })).ToList();
        Assert.Single(previousHome);
        Assert.Equal("OTHER-1", previousHome[0].Identifier);

        // Unfiltered paging is unaffected.
        var all = (await repository.GetFlowExecutionsAsync(new ExecutionQuery())).ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task StatusAndSearch_CombineAsAnd()
    {
        var (repository, connection) = await SeedAsync();
        using var _ = connection;

        var failed = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { Status = ExecutionStatus.Failed })).ToList();
        Assert.Single(failed);
        Assert.Equal("ORD-1002", failed[0].Identifier);

        var contradictory = (await repository.GetFlowExecutionsAsync(new ExecutionQuery
        {
            Status = ExecutionStatus.Failed,
            Search = "SKU-77"
        })).ToList();
        Assert.Empty(contradictory);

        var combined = (await repository.GetFlowExecutionsAsync(new ExecutionQuery
        {
            Status = ExecutionStatus.Success,
            Search = "Order 1001"
        })).ToList();
        Assert.Single(combined);
        Assert.Equal("ORD-1001", combined[0].Identifier);
    }
}


