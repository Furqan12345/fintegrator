// Fusetor persistence benchmark. Runs against a scratch SQLite file it creates itself; it never
// touches ipaas.db. Usage: dotnet run -c Release --project tools/FusetorBench -- <label> [dbPath] [executions]
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

var label = args.Length > 0 ? args[0] : "run";
var dbPath = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "fusetor-bench.db");
var executionCount = args.Length > 2 ? int.Parse(args[2]) : 60_000;

foreach (var suffix in new[] { "", "-wal", "-shm" })
    if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);

DbContextOptions<IPaaSContext> Options() => BenchDb.Options(dbPath);
IPaaSContext NewContext() => new(Options());

using (var ctx = NewContext()) DatabaseSchemaInitializer.EnsureUpToDate(ctx);

var rnd = new Random(42);
var flowIds = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToArray();
var stepIds = flowIds.ToDictionary(f => f, _ => Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray());
using (var ctx = NewContext())
{
    var integration = new Integration { Name = "Bench integration" };
    ctx.Integrations.Add(integration);
    foreach (var flowId in flowIds)
    {
        ctx.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, IntegrationId = integration.Id, Name = "Bench flow" });
        for (var s = 0; s < 3; s++)
            ctx.IntegrationSteps.Add(new IntegrationStep { Id = stepIds[flowId][s], FlowId = flowId, NodeName = "Step" + s });
    }
    await ctx.SaveChangesAsync();
}
var payload = new string('x', 900);
var seedWatch = Stopwatch.StartNew();
for (var offset = 0; offset < executionCount; offset += 5000)
{
    using var ctx = NewContext();
    ctx.ChangeTracker.AutoDetectChangesEnabled = false;
    var count = Math.Min(5000, executionCount - offset);
    for (var i = 0; i < count; i++)
    {
        var execution = new FlowExecution
        {
            FlowId = flowIds[rnd.Next(flowIds.Length)],
            FlowName = "Bench flow " + rnd.Next(40),
            IntegrationName = "Bench integration",
            Status = rnd.Next(100) < 92 ? ExecutionStatus.Success : ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddSeconds(-(executionCount - offset - i)),
            QueuedAt = DateTime.UtcNow.AddSeconds(-(executionCount - offset - i) - 1),
            CompletedAt = DateTime.UtcNow,
            TriggerSource = "Webhook",
            TriggerPayloadJson = payload,
            Message = "done",
        };
        ctx.FlowExecutions.Add(execution);
        for (var s = 0; s < 3; s++)
            ctx.StepExecutions.Add(new StepExecution
            {
                FlowExecutionId = execution.Id, StepId = stepIds[execution.FlowId][s], NodeName = "Step" + s,
                Status = ExecutionStatus.Success, ReceivedInput = payload, RequestPayload = payload, ResponsePayload = payload,
            });
        if (i % 3 == 0)
            ctx.DeadLetterEntries.Add(new DeadLetterEntry
            {
                FlowExecutionId = execution.Id, StepId = stepIds[execution.FlowId][0], FlowName = execution.FlowName, NodeName = "Step0",
                Payload = payload, ErrorMessage = "boom", Status = rnd.Next(100) < 10 ? "Pending" : "Retried",
                CreatedAt = execution.StartedAt,
            });
    }
    await ctx.SaveChangesAsync();
}
Console.WriteLine($"seeded {executionCount} executions in {seedWatch.Elapsed.TotalSeconds:N1}s");
// What the next host start does: refresh planner statistics (PRAGMA optimize) so measurements reflect a warmed database.
using (var ctx = NewContext()) { var c = ctx.Database.GetDbConnection(); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA optimize=0x10002;"; cmd.ExecuteNonQuery(); }

if (Environment.GetEnvironmentVariable("BENCH_PRINT_SQL") == "1")
{
    using var sqlCtx = NewContext();
    Console.WriteLine("-- manual " + sqlCtx.DeadLetterEntries.AsNoTracking()
        .Where(entry => entry.ReplayRequestedAt != null && !sqlCtx.FlowExecutions.Any(run => run.Id == entry.FlowExecutionId && run.IsTest))
        .OrderBy(entry => entry.CreatedAt).Take(50).ToQueryString());
    Console.WriteLine("-- pending " + sqlCtx.DeadLetterEntries.AsNoTracking()
        .Where(entry => entry.Status == "Pending" && !sqlCtx.FlowExecutions.Any(run => run.Id == entry.FlowExecutionId && run.IsTest))
        .OrderBy(entry => entry.CreatedAt).Take(50).ToQueryString());
    return;
}
var results = new Dictionary<string, object>();
async Task Measure(string name, int iterations, Func<int, Task> action)
{
    for (var i = 0; i < Math.Min(3, iterations); i++) await action(-1 - i); // warm-up
    var samples = new List<double>(iterations);
    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        await action(i);
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }
    samples.Sort();
    var median = samples[samples.Count / 2];
    var p95 = samples[(int)(samples.Count * 0.95)];
    results[name] = new { medianMs = Math.Round(median, 3), p95Ms = Math.Round(p95, 3) };
    Console.WriteLine($"{name,-34} median {median,9:N3} ms   p95 {p95,9:N3} ms");
}

// 1. Idle claim poll (nothing queued): this is what each worker runs every PollIntervalSeconds.
await Measure("claim_poll_empty", 300, async _ =>
{
    using var ctx = NewContext();
    await new ExecutionRepository(ctx).TryClaimNextQueuedExecutionAsync();
});

// 2. Claim with a queue present.
using (var ctx = NewContext())
{
    for (var i = 0; i < 80; i++)
        ctx.FlowExecutions.Add(new FlowExecution { FlowId = flowIds[0], FlowName = "Queued", Status = ExecutionStatus.Queued, QueuedAt = DateTime.UtcNow.AddSeconds(i), TriggerSource = "Webhook" });
    await ctx.SaveChangesAsync();
}
await Measure("claim_with_queue", 75, async _ =>
{
    using var ctx = NewContext();
    await new ExecutionRepository(ctx).TryClaimNextQueuedExecutionAsync();
});

// 3. UI list queries.
await Measure("execution_list_page1", 100, async _ =>
{
    using var ctx = NewContext();
    await new ExecutionRepository(ctx).GetFlowExecutionsAsync(new ExecutionQuery { PageSize = 50 });
});
await Measure("execution_list_flow_filter", 100, async _ =>
{
    using var ctx = NewContext();
    await new ExecutionRepository(ctx).GetFlowExecutionsAsync(new ExecutionQuery { FlowId = flowIds[3], PageSize = 50 });
});
await Measure("execution_list_failed_filter", 100, async _ =>
{
    using var ctx = NewContext();
    await new ExecutionRepository(ctx).GetFlowExecutionsAsync(new ExecutionQuery { Status = ExecutionStatus.Failed, PageSize = 50 });
});

// 4. Dead-letter scans used by the Engine every minute and by the UI.
await Measure("dead_letter_pending_batch", 60, async _ =>
{
    using var ctx = NewContext();
    await new ExecutionRepository(ctx).GetPendingDeadLettersAsync(100);
});
await Measure("dead_letter_replayable_batch", 60, async _ =>
{
    using var ctx = NewContext();
    await new ExecutionRepository(ctx).GetReplayableDeadLettersAsync(50);
});

// 5. Step persistence: a card = add + two updates, each its own commit (what FlowExecutor does today).
var runId = Guid.NewGuid();
using (var ctx = NewContext()) { ctx.FlowExecutions.Add(new FlowExecution { Id = runId, FlowId = flowIds[0], FlowName = "Step bench", TriggerSource = "Webhook" }); await ctx.SaveChangesAsync(); }
await Measure("step_add_plus_2_updates", 300, async _ =>
{
    using var ctx = NewContext();
    var repo = new ExecutionRepository(ctx);
    var step = new StepExecution { FlowExecutionId = runId, StepId = stepIds[flowIds[0]][0], NodeName = "S", ReceivedInput = payload };
    await repo.AddStepExecutionAsync(step);
    step.RequestPayload = payload; await repo.UpdateStepExecutionAsync(step);
    step.ResponsePayload = payload; step.Status = ExecutionStatus.Success; step.CompletedAt = DateTime.UtcNow; await repo.UpdateStepExecutionAsync(step);
});

// 6. Log sink throughput through the real writer.
var logRows = 100_000;
var logOptions = new DatabaseLoggerOptions { MaxRows = 0, RetentionHours = 0, WarningRetentionHours = 0, QueueCapacity = 100_000 };
using (var writer = new DatabaseLogWriter(logOptions, "Data Source=" + dbPath))
{
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < logRows; i++)
        writer.TryEnqueue(new AppLogEntry { Level = i % 9 == 0 ? "Warning" : "Information", Service = "Engine", Source = "bench", Category = "Bench", EventName = "BenchEvent", Outcome = "Success", Message = "benchmark log line " + i, TraceId = Guid.NewGuid().ToString("N"), FlowExecutionId = runId, FlowId = flowIds[0] });
    writer.Flush();
    var seconds = sw.Elapsed.TotalSeconds;
    results["log_rows_per_second"] = new { rate = Math.Round(logRows / seconds) };
    Console.WriteLine($"{"log_rows_per_second",-34} {logRows / seconds,9:N0} rows/s   (written {writer.WrittenCount}, dropped {writer.DroppedCount})");
}

// 6b. Log console queries on the table the sink just filled.
await Measure("log_page_default", 40, async _ =>
{
    using var ctx = NewContext();
    await new LogRepository(ctx).GetPageAsync(new LogQuery(), 1, 100);
});
await Measure("log_page_level_warning", 40, async _ =>
{
    using var ctx = NewContext();
    await new LogRepository(ctx).GetPageAsync(new LogQuery { MinimumLevel = "Warning" }, 1, 100);
});
await Measure("log_page_service_filter", 40, async _ =>
{
    using var ctx = NewContext();
    await new LogRepository(ctx).GetPageAsync(new LogQuery { Service = "Engine" }, 1, 100);
});
await Measure("log_facets", 10, async _ =>
{
    using var ctx = NewContext();
    await new LogRepository(ctx).GetFacetsAsync(new LogQuery());
});

// 7. Four concurrent claimers draining a queue (mirrors MaxConcurrency=4).
using (var ctx = NewContext())
{
    for (var i = 0; i < 400; i++)
        ctx.FlowExecutions.Add(new FlowExecution { FlowId = flowIds[0], FlowName = "Queued2", Status = ExecutionStatus.Queued, QueuedAt = DateTime.UtcNow.AddSeconds(i), TriggerSource = "Webhook" });
    await ctx.SaveChangesAsync();
}
{
    var claimed = 0;
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
    {
        while (true)
        {
            using var ctx = NewContext();
            var claim = await new ExecutionRepository(ctx).TryClaimNextQueuedExecutionAsync();
            if (claim == null) return;
            Interlocked.Increment(ref claimed);
        }
    })));
    var rate = claimed / sw.Elapsed.TotalSeconds;
    results["concurrent_claims_per_second"] = new { rate = Math.Round(rate), claimed };
    Console.WriteLine($"{"concurrent_claims_per_second",-34} {rate,9:N0} claims/s (claimed {claimed})");
}

// 9. End-to-end flow runs through the real FlowExecutor (3 mapping cards with scripts, real repositories, real DB).
{
    var e2eFlowId = Guid.NewGuid();
    using (var ctx = NewContext())
    {
        var flow = new IntegrationFlow { Id = e2eFlowId, Name = "E2E", Status = FlowStatus.Active, TriggerType = TriggerType.Manual };
        var nodes = Enumerable.Range(1, 3).Select(n => new IntegrationStep
        {
            Id = Guid.NewGuid(), FlowId = e2eFlowId, StepType = StepType.Mapping, NodeName = "Map" + n,
            MappingCode = "var doc = JObject.Parse(FlowStateJson); return new JObject { [\"step\"] = " + n + ", [\"keys\"] = doc.Count }.ToString(Formatting.None);"
        }).ToList();
        foreach (var node in nodes) flow.Nodes.Add(node);
        for (var i = 0; i < nodes.Count - 1; i++)
            flow.Edges.Add(new IntegrationEdge { FlowId = e2eFlowId, SourceNodeId = nodes[i].Id, TargetNodeId = nodes[i + 1].Id });
        ctx.IntegrationFlows.Add(flow);
        await ctx.SaveChangesAsync();
    }

    async Task<ExecutionStatus> RunOne()
    {
        var execId = Guid.NewGuid();
        using var ctx = NewContext();
        ctx.FlowExecutions.Add(new FlowExecution { Id = execId, FlowId = e2eFlowId, FlowName = "E2E", TriggerSource = "Test", Status = ExecutionStatus.InProgress });
        await ctx.SaveChangesAsync();
        var executor = new FlowExecutor(
            new IntegrationRepository(ctx), new NoTransport(), new AdvancedCodeExecutionService(new ConfigurationBuilder().Build()),
            new ExecutionRepository(ctx), new CrossReferenceRepository(ctx), new NoEmail(), NullLogger<FlowExecutor>.Instance);
        var result = await executor.ExecuteFlowAsync(e2eFlowId, execId, "{\"order\":1}", CancellationToken.None);
        return result.Status;
    }

    var first = await RunOne();
    Console.WriteLine($"e2e first run status: {first}");
    for (var i = 0; i < 3; i++) await RunOne();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < 150; i++) await RunOne();
    var seq = 150 / sw.Elapsed.TotalSeconds;
    sw.Restart();
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => { for (var i = 0; i < 75; i++) await RunOne(); })));
    var par = 300 / sw.Elapsed.TotalSeconds;
    results["e2e_flow_runs_per_second_sequential"] = new { rate = Math.Round(seq, 1) };
    results["e2e_flow_runs_per_second_4_parallel"] = new { rate = Math.Round(par, 1) };
    Console.WriteLine($"{"e2e_3_card_flow_sequential",-34} {seq,9:N1} runs/s ({1000 / seq:N1} ms/run)");
    Console.WriteLine($"{"e2e_3_card_flow_4_parallel",-34} {par,9:N1} runs/s");
}

// 8. Script compilation: fresh ScriptOptions per compile (the old code) vs one shared instance (the new code).
{
    ScriptOptions Fresh() => ScriptOptions.Default
        .AddReferences(typeof(object).Assembly, typeof(System.Linq.Enumerable).Assembly, typeof(System.Collections.Generic.List<>).Assembly,
            typeof(System.Text.Json.JsonDocument).Assembly, typeof(System.Text.Json.Nodes.JsonNode).Assembly, typeof(Newtonsoft.Json.JsonConvert).Assembly)
        .AddImports("System", "System.Linq", "System.Collections.Generic", "System.Text.Json", "Newtonsoft.Json", "Newtonsoft.Json.Linq");
    var shared = Fresh();
    var coldWatch = Stopwatch.StartNew();
    CSharpScript.Create<string>("return \"cold\";", shared).CreateDelegate();
    results["script_first_compile_cold_ms"] = Math.Round(coldWatch.Elapsed.TotalMilliseconds);
    Console.WriteLine($"{"script_first_compile_cold",-30} {coldWatch.Elapsed.TotalMilliseconds,8:N0} ms (first Roslyn compile in the process)");
    for (var i = 0; i < 6; i++) CSharpScript.Create<string>($"return \"warm{i}\";", shared).CreateDelegate();   // JIT warm-up
    var round = 0;
    foreach (var mode in new[] { "fresh_options", "shared_options", "fresh_options", "shared_options" })
    {
        round++;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var managedBefore = GC.GetTotalMemory(true);
        var wsBefore = Process.GetCurrentProcess().WorkingSet64;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++)
            CSharpScript.Create<string>($"var a = {i}; return \"v\" + a + \"{mode}{round}\";", mode == "fresh_options" ? Fresh() : shared).CreateDelegate();
        var ms = sw.Elapsed.TotalMilliseconds / 60;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var managed = (GC.GetTotalMemory(true) - managedBefore) / 1048576.0;
        var ws = (Process.GetCurrentProcess().WorkingSet64 - wsBefore) / 1048576.0;
        results["script_compile_" + mode + round] = new { msPerCompile = Math.Round(ms, 1), retainedManagedMb = Math.Round(managed, 1), workingSetGrowthMb = Math.Round(ws, 1) };
        Console.WriteLine($"{"script_compile_" + mode,-30} #{round} {ms,8:N1} ms/compile   retained managed +{managed:N1} MB   working set +{ws:N1} MB (60 scripts)");
    }
}

using (var ctx = NewContext())
{
    var conn = ctx.Database.GetDbConnection(); conn.Open();
    using var cmd = conn.CreateCommand(); cmd.CommandText = "PRAGMA synchronous;";
    results["synchronous"] = cmd.ExecuteScalar()!;
}
var proc = Process.GetCurrentProcess();
results["peak_working_set_mb"] = proc.PeakWorkingSet64 / 1048576;
results["gc_total_mb"] = GC.GetTotalMemory(false) / 1048576;
results["db_file_mb"] = new FileInfo(dbPath).Length / 1048576;
Console.WriteLine($"peak working set {proc.PeakWorkingSet64 / 1048576} MB, managed {GC.GetTotalMemory(false) / 1048576} MB, db {new FileInfo(dbPath).Length / 1048576} MB");

var outFile = Path.Combine(AppContext.BaseDirectory, $"bench-{label}.json");
File.WriteAllText(outFile, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("results: " + outFile);

// Builds the same options the hosts use. Kept in one place so a persistence change shows up here.
static class BenchDb
{
    public static DbContextOptions<IPaaSContext> Options(string dbPath) =>
        new DbContextOptionsBuilder<IPaaSContext>().UseTunedSqlite("Data Source=" + dbPath).Options;
}

sealed class NoTransport : ITransportEngine
{
    public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default, SimpleIPaaS.Application.Models.FlowTestContext? testContext = null)
        => throw new NotSupportedException("the benchmark flow has no HTTP card");
}

sealed class NoEmail : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
