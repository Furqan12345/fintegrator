using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class PacketLogPageTests
{

    [Fact]
    public async Task GetPacketLogPage_ReturnsFlowIntegrationAndConnectionContext()
    {
        using var connection = new SqliteConnection("Data Source=PacketLogPage;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var connectionId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var stepExecutionId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.IntegrationFlows.Add(new IntegrationFlow
            {
                Id = flowId,
                Name = "Shopify Orders"
            });
            context.Connections.Add(new Connection
            {
                Id = connectionId,
                Name = "Shopify Conn",
                BaseUrl = "https://shop.example.test",
                AuthType = AuthType.Bearer
            });
            context.IntegrationSteps.Add(new IntegrationStep
            {
                Id = nodeId,
                FlowId = flowId,
                NodeName = "FetchShopifyOrders",
                StepType = StepType.HttpAction,
                ConnectionId = connectionId
            });
            context.FlowExecutions.Add(new FlowExecution
            {
                Id = runId,
                FlowId = flowId,
                FlowName = "Shopify Orders",
                IntegrationName = "Shopify sync"
            });
            context.StepExecutions.Add(new StepExecution
            {
                Id = stepExecutionId,
                FlowExecutionId = runId,
                StepId = nodeId,
                NodeName = "FetchShopifyOrders",
                Status = ExecutionStatus.Success
            });
            context.StepPacketLogs.Add(new StepPacketLog
            {
                Id = Guid.NewGuid(),
                StepExecutionId = stepExecutionId,
                Sequence = 1,
                Kind = "HTTP",
                HttpMethod = "POST",
                RequestUrl = "https://shop.example.test/graphql.json",
                StatusCode = 200,
                StartedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        using (var context = new IPaaSContext(options))
        {
            var repository = new ExecutionRepository(context);

            var all = await repository.GetPacketLogPageAsync(new PacketLogPageFilter());
            var row = Assert.Single(all);
            Assert.Equal("Shopify Orders", row.FlowName);
            Assert.Equal("Shopify sync", row.IntegrationName);
            Assert.Equal("FetchShopifyOrders", row.NodeName);
            Assert.Equal("Shopify Conn", row.ConnectionName);
            Assert.Equal(runId, row.FlowExecutionId);
            Assert.Equal(200, row.StatusCode);

            var bySearch = await repository.GetPacketLogPageAsync(new PacketLogPageFilter(Search: "graphql"));
            Assert.Single(bySearch);
            var noMatch = await repository.GetPacketLogPageAsync(new PacketLogPageFilter(Search: "stripe"));
            Assert.Empty(noMatch);

            var byConnection = await repository.GetPacketLogPageAsync(new PacketLogPageFilter(ConnectionId: connectionId));
            Assert.Single(byConnection);
            var manualOnly = await repository.GetPacketLogPageAsync(new PacketLogPageFilter(ManualOnly: true));
            Assert.Empty(manualOnly);
            var failed = await repository.GetPacketLogPageAsync(new PacketLogPageFilter(Outcome: "failure"));
            Assert.Empty(failed);

            var detail = await repository.GetPacketLogDetailAsync(row.PacketId);
            Assert.NotNull(detail);
            Assert.Equal("https://shop.example.test/graphql.json", detail!.RequestUrl);
            Assert.Equal("Shopify Conn", detail.ConnectionName);
        }
    }
}



