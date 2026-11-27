using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Tests.Persistence;

// Each test case keeps its newest runs (three) for comparing; older runs go with their test transactions.
public sealed class FlowTestRunTrimTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public FlowTestRunTrimTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(_connection).Options;
        using var context = new IPaaSContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Trimming_keeps_the_newest_three_runs_and_deletes_the_older_runs_test_transactions()
    {
        Assert.Equal(3, IFlowTestRepository.KeptRunsPerTestCase);
        var flowId = Guid.NewGuid();
        var testCaseId = Guid.NewGuid();
        var otherCaseId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddHours(-1);
        var runs = Enumerable.Range(0, 5).Select(i => (Run: Guid.NewGuid(), Execution: Guid.NewGuid(), Step: Guid.NewGuid(), At: start.AddMinutes(i))).ToList();
        var otherExecution = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        await using (var context = new IPaaSContext(_options))
        {
            context.IntegrationFlows.Add(new IntegrationFlow
            {
                Id = flowId,
                Name = "Orders",
                Nodes = { new IntegrationStep { Id = nodeId, FlowId = flowId, StepType = StepType.HttpAction, NodeName = "Fetch" } }
            });
            context.FlowTestCases.Add(new FlowTestCase { Id = testCaseId, FlowId = flowId, Name = "Happy path" });
            context.FlowTestCases.Add(new FlowTestCase { Id = otherCaseId, FlowId = flowId, Name = "Other" });
            foreach (var item in runs)
            {
                context.FlowExecutions.Add(new FlowExecution { Id = item.Execution, FlowId = flowId, IsTest = true, Status = ExecutionStatus.Success, StartedAt = item.At, CompletedAt = item.At });
                context.StepExecutions.Add(new StepExecution { Id = item.Step, FlowExecutionId = item.Execution, StepId = nodeId, StartedAt = item.At });
                context.StepPacketLogs.Add(new StepPacketLog { StepExecutionId = item.Step, Kind = "HTTP", StartedAt = item.At, ResponseBody = "body" });
                context.FlowTestRuns.Add(new FlowTestRun { Id = item.Run, TestCaseId = testCaseId, FlowId = flowId, FlowExecutionId = item.Execution, Status = "Passed", StartedAt = item.At });
            }

            // Another test case's run is not touched.
            context.FlowExecutions.Add(new FlowExecution { Id = otherExecution, FlowId = flowId, IsTest = true, Status = ExecutionStatus.Success, StartedAt = start });
            context.FlowTestRuns.Add(new FlowTestRun { TestCaseId = otherCaseId, FlowId = flowId, FlowExecutionId = otherExecution, Status = "Passed", StartedAt = start });
            await context.SaveChangesAsync();
        }

        await using (var context = new IPaaSContext(_options))
        {
            await new FlowTestRepository(context).TrimRunsAsync(testCaseId);
        }

        await using (var verify = new IPaaSContext(_options))
        {
            var kept = runs.Skip(2).ToList();
            var gone = runs.Take(2).ToList();
            Assert.Equal(kept.Select(item => item.Run).OrderBy(id => id), (await verify.FlowTestRuns.Where(run => run.TestCaseId == testCaseId).Select(run => run.Id).ToListAsync()).OrderBy(id => id));
            Assert.Equal(3, await verify.FlowExecutions.CountAsync(run => kept.Select(item => item.Execution).Contains(run.Id)));
            Assert.Equal(0, await verify.FlowExecutions.CountAsync(run => gone.Select(item => item.Execution).Contains(run.Id)));
            Assert.Equal(0, await verify.StepExecutions.CountAsync(step => gone.Select(item => item.Step).Contains(step.Id)));
            Assert.Equal(0, await verify.StepPacketLogs.CountAsync(packet => gone.Select(item => item.Step).Contains(packet.StepExecutionId)));
            Assert.Equal(3, await verify.StepPacketLogs.CountAsync(packet => kept.Select(item => item.Step).Contains(packet.StepExecutionId)));
            Assert.Equal(1, await verify.FlowTestRuns.CountAsync(run => run.TestCaseId == otherCaseId));
            Assert.Equal(1, await verify.FlowExecutions.CountAsync(run => run.Id == otherExecution));
        }
    }
}
