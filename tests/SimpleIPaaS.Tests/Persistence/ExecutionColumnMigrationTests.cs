using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class ExecutionColumnMigrationTests
{
    [Fact]
    public void EnsureUpToDate_AddsIdentifierAndMessageColumnsToExistingFlowExecutions()
    {
        using var connection = new SqliteConnection("Data Source=ExecutionColumnsMigration;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
        }
        // Simulate a database created before the Identifier/Message columns existed.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE FlowExecutions DROP COLUMN Identifier; ALTER TABLE FlowExecutions DROP COLUMN Message;";
            command.ExecuteNonQuery();
        }

        var flowId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);

            context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = "Migration flow" });
            context.FlowExecutions.Add(new FlowExecution
            {
                FlowId = flowId,
                FlowName = "Migration check",
                TriggerSource = "Test",
                Identifier = "ORD-1",
                Message = "Synced successfully"
            });
            context.SaveChanges();
        }
        using (var context = new IPaaSContext(options))
        {
            var row = Assert.Single(context.FlowExecutions);
            Assert.Equal("ORD-1", row.Identifier);
            Assert.Equal("Synced successfully", row.Message);
        }
    }

    [Fact]
    public void EnsureUpToDate_AddsSuiteRunIdToExistingFlowTestRuns()
    {
        using var connection = new SqliteConnection("Data Source=FlowTestSuiteMigration;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE FlowTestRuns DROP COLUMN SuiteRunId;";
            command.ExecuteNonQuery();
        }

        var flowId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var suiteId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);
            context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = "Suite migration flow" });
            context.FlowTestCases.Add(new FlowTestCase
            {
                Id = caseId,
                FlowId = flowId,
                Name = "ZZ Test - migration",
                DefinitionJson = "{}"
            });
            context.FlowTestRuns.Add(new FlowTestRun
            {
                Id = runId,
                FlowId = flowId,
                TestCaseId = caseId,
                SuiteRunId = suiteId,
                FlowExecutionId = Guid.NewGuid(),
                Status = "Passed",
                StartedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }
        using (var context = new IPaaSContext(options))
        {
            var run = Assert.Single(context.FlowTestRuns);
            Assert.Equal(suiteId, run.SuiteRunId);
        }
    }
}

