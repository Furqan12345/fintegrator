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

public sealed class DeadLetterReplayOrderingTests
{
    [Fact]
    public async Task ReplayScanReturnsManualRequestAheadOfOlderPendingBacklog()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(connection)
            .Options;
        Guid manualId;

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();

            var flow = new IntegrationFlow
            {
                Id = Guid.NewGuid(),
                Name = "Ordering flow"
            };
            var node = new IntegrationStep
            {
                Id = Guid.NewGuid(),
                FlowId = flow.Id,
                NodeName = "Push",
                StepType = StepType.HttpAction,
                HttpMethod = "GET",
                EndpointUrl = "https://example.test/orders"
            };
            flow.Nodes.Add(node);
            context.IntegrationFlows.Add(flow);

            var execution = new FlowExecution
            {
                Id = Guid.NewGuid(),
                FlowId = flow.Id,
                FlowName = flow.Name,
                Status = ExecutionStatus.Failed,
                TriggerSource = "Test"
            };
            context.FlowExecutions.Add(execution);

            var old = DateTime.UtcNow.AddHours(-2);
            for (var index = 0; index < 150; index++)
            {
                context.DeadLetterEntries.Add(new DeadLetterEntry
                {
                    Id = Guid.NewGuid(),
                    FlowExecutionId = execution.Id,
                    StepId = node.Id,
                    FlowName = flow.Name,
                    NodeName = "Push",
                    Status = "Pending",
                    CreatedAt = old.AddSeconds(index)
                });
            }

            var manual = new DeadLetterEntry
            {
                Id = Guid.NewGuid(),
                FlowExecutionId = execution.Id,
                StepId = node.Id,
                FlowName = flow.Name,
                NodeName = "Push",
                Status = "Retrying",
                CreatedAt = DateTime.UtcNow,
                ReplayRequestedAt = DateTime.UtcNow
            };
            context.DeadLetterEntries.Add(manual);
            context.SaveChanges();
            manualId = manual.Id;
        }

        using (var context = new IPaaSContext(options))
        {
            var repository = new ExecutionRepository(context);

            var rows = await repository.GetReplayableDeadLettersAsync(100);

            Assert.Equal(100, rows.Count);
            Assert.Equal(manualId, rows[0].Id);
            Assert.Contains(rows, row => row.Id == manualId);
        }
    }
}


