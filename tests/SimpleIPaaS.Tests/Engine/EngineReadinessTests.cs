using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Services;

namespace SimpleIPaaS.Tests.Engine;

// Regression tests for the Engine production-readiness pass (see ops/ENGINE-READINESS.md).
public sealed class EngineReadinessTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public EngineReadinessTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(_connection).Options;
        using var context = new IPaaSContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static AdvancedCodeExecutionService Scripts(int timeoutSeconds) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scripting:TimeoutSeconds"] = timeoutSeconds.ToString()
        }).Build());

    // ---- runaway scripts: the timeout must stop the loop, not just stop waiting for it

    [Theory]
    [InlineData("while (true) { }")]
    [InlineData("for (;;) ;")]
    [InlineData("do { } while (true);")]
    [InlineData("foreach (var x in Enumerable.Repeat(1, int.MaxValue)) { }")]
    [InlineData("spin: goto spin;")]
    [InlineData("var n = 0; while (true) { for (var i = 0; i < 10; i++) n++; }")]
    public async Task Runaway_loop_times_out_and_its_thread_is_released(string code)
    {
        var scripts = Scripts(1);
        var error = await Assert.ThrowsAsync<ScriptExecutionException>(() => scripts.ExecuteMappingAsync(code, "{}", "{}"));
        Assert.True(error.TimedOut);

        // The loop itself must stop (not just the wait for it): run the instrumented script against an already
        // cancelled token. Without the guard this never returns and the wait below times out.
        var instrumented = ScriptLoopGuard.Instrument(code);
        var script = Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.Create<object>(instrumented,
            Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default.AddReferences(typeof(Enumerable).Assembly).AddImports("System", "System.Linq"),
            typeof(AdvancedScriptGlobals));
        var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        var run = Task.Run(() => script.RunAsync(new AdvancedScriptGlobals { CancellationToken = cancelled.Token }));
        Assert.True(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2))) == run, "the loop kept running after cancellation");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Loop_guard_leaves_normal_scripts_and_error_line_numbers_alone()
    {
        var scripts = Scripts(10);
        var result = await scripts.ExecuteMappingAsync(
            "var total = 0;\nforeach (var i in Enumerable.Range(1, 100)) total += i;\nwhile (total > 6000) total--;\nreturn total.ToString();", "{}", "{}");
        Assert.Equal("5050", result);

        var error = await Assert.ThrowsAsync<ScriptExecutionException>(() => scripts.ExecuteMappingAsync(
            "var a = 1;\nwhile (a < 3) { a++; }\nreturn undefinedThing;", "{}", "{}"));
        Assert.Contains("(3,", error.Message);
    }

    // ---- claims

    [Fact]
    public async Task Claim_skips_flows_already_running_and_a_released_claim_is_queued_again()
    {
        var (flowA, flowB) = (await SeedFlowAsync("A"), await SeedFlowAsync("B"));
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        await using (var context = new IPaaSContext(_options))
        {
            context.FlowExecutions.AddRange(
                new FlowExecution { Id = runA, FlowId = flowA, Status = ExecutionStatus.Queued, QueuedAt = DateTime.UtcNow.AddSeconds(-10) },
                new FlowExecution { Id = runB, FlowId = flowB, Status = ExecutionStatus.Queued, QueuedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        await using (var context = new IPaaSContext(_options))
        {
            var repository = new ExecutionRepository(context);
            var claim = await repository.TryClaimNextQueuedExecutionAsync(new[] { flowA });
            Assert.Equal(runB, claim!.ExecutionId);
            Assert.True(await repository.ReleaseClaimAsync(runB));
            Assert.False(await repository.ReleaseClaimAsync(runB));
        }

        await using (var verify = new IPaaSContext(_options))
        {
            Assert.Equal(ExecutionStatus.Queued, (await verify.FlowExecutions.FindAsync(runB))!.Status);
        }
    }

    // ---- crash recovery

    [Fact]
    public async Task Stale_runs_are_failed_with_their_steps_and_live_or_test_runs_are_left_alone()
    {
        var flow = await SeedFlowAsync("Recovery");
        var nodeId = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var live = Guid.NewGuid();
        var testRun = Guid.NewGuid();
        await using (var context = new IPaaSContext(_options))
        {
            context.IntegrationSteps.Add(new IntegrationStep { Id = nodeId, FlowId = flow, NodeName = "Call", StepType = StepType.HttpAction });
            context.FlowExecutions.AddRange(
                new FlowExecution { Id = orphan, FlowId = flow, Status = ExecutionStatus.InProgress, StartedAt = DateTime.UtcNow.AddMinutes(-30) },
                new FlowExecution { Id = live, FlowId = flow, Status = ExecutionStatus.InProgress, StartedAt = DateTime.UtcNow.AddSeconds(-5) },
                new FlowExecution { Id = testRun, FlowId = flow, Status = ExecutionStatus.InProgress, IsTest = true, StartedAt = DateTime.UtcNow.AddMinutes(-3) });
            context.StepExecutions.Add(new StepExecution { FlowExecutionId = orphan, StepId = nodeId, Status = ExecutionStatus.InProgress });
            await context.SaveChangesAsync();
        }

        await using (var context = new IPaaSContext(_options))
        {
            var failed = await new ExecutionRepository(context).FailStaleExecutionsAsync(
                DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-10), "engine died");
            Assert.Equal(new[] { orphan }, failed);
        }

        await using (var verify = new IPaaSContext(_options))
        {
            var row = await verify.FlowExecutions.FindAsync(orphan);
            Assert.Equal(ExecutionStatus.Failed, row!.Status);
            Assert.Equal("engine died", row.ErrorMessage);
            Assert.NotNull(row.CompletedAt);
            Assert.Equal(ExecutionStatus.Failed, (await verify.StepExecutions.SingleAsync(step => step.FlowExecutionId == orphan)).Status);
            Assert.Equal(ExecutionStatus.InProgress, (await verify.FlowExecutions.FindAsync(live))!.Status);
            Assert.Equal(ExecutionStatus.InProgress, (await verify.FlowExecutions.FindAsync(testRun))!.Status);
        }
    }

    // ---- persisted state is read from the database, not from the context's copy

    [Fact]
    public async Task Persisted_state_read_returns_the_stored_value_even_when_the_flow_is_already_tracked()
    {
        var flow = await SeedFlowAsync("Counter");
        await using var runContext = new IPaaSContext(_options);
        var repository = new IntegrationRepository(runContext);
        _ = await runContext.IntegrationFlows.FirstAsync(f => f.Id == flow); // tracked with PersistedStateJson "{}"

        await using (var other = new IPaaSContext(_options))
        {
            await new IntegrationRepository(other).UpdatePersistedStateAsync(flow, "{\"count\":7}");
        }

        Assert.Equal("{\"count\":7}", await repository.GetPersistedStateAsync(flow));
    }

    // ---- cron: one firing, one winner

    [Fact]
    public async Task Cron_firing_is_taken_by_exactly_one_scheduler()
    {
        var flow = await SeedFlowAsync("Cron");
        var due = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        await using (var context = new IPaaSContext(_options))
        {
            await new IntegrationRepository(context).UpdateNextRunAtAsync(flow, due);
        }

        await using var first = new IPaaSContext(_options);
        await using var second = new IPaaSContext(_options);
        var wins = new[]
        {
            await new IntegrationRepository(first).TryAdvanceNextRunAtAsync(flow, due, due.AddMinutes(1)),
            await new IntegrationRepository(second).TryAdvanceNextRunAtAsync(flow, due, due.AddMinutes(1)),
        };
        Assert.Equal(1, wins.Count(won => won));
    }

    // ---- the flow gate (in-process half; the PostgreSQL advisory half is covered by the scale-out suite)

    [Fact]
    public async Task Flow_gate_admits_one_run_per_flow_and_reopens_on_release()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var gate = new SimpleIPaaS.Engine.FlowRunGate(services, Microsoft.Extensions.Logging.Abstractions.NullLogger<SimpleIPaaS.Engine.FlowRunGate>.Instance);
        var flow = Guid.NewGuid();
        var lease = await gate.TryEnterAsync(flow);
        Assert.NotNull(lease);
        Assert.Null(await gate.TryEnterAsync(flow));
        Assert.NotNull(await gate.TryEnterAsync(Guid.NewGuid()));
        await lease!.DisposeAsync();
        Assert.NotNull(await gate.TryEnterAsync(flow));
    }

    private async Task<Guid> SeedFlowAsync(string name)
    {
        var id = Guid.NewGuid();
        await using var context = new IPaaSContext(_options);
        context.IntegrationFlows.Add(new IntegrationFlow { Id = id, Name = name });
        await context.SaveChangesAsync();
        return id;
    }
}
