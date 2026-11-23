using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Api.Hosting;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Tests.Performance;

// Guards for the optimization work: the indexes the hot queries depend on, the per-connection SQLite
// settings, the rewritten dead-letter scan, the cached log facets, the precompressed static files and the
// page-visibility service that pauses background polling.
public sealed class Tier1PerformanceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fusetor-perf-" + Guid.NewGuid().ToString("N"));

    public Tier1PerformanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string NewDatabase(out DbContextOptions<IPaaSContext> options)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db");
        options = new DbContextOptionsBuilder<IPaaSContext>().UseTunedSqlite("Data Source=" + path).Options;
        using var context = new IPaaSContext(options);
        DatabaseSchemaInitializer.EnsureUpToDate(context);
        return path;
    }

    private static List<string> Indexes(string path, string table)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{table}\");";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    private static string Plan(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read()) lines.Add(reader.GetString(3));
        return string.Join(" | ", lines);
    }

    [Fact]
    public void FreshDatabase_HasThePerformanceIndexes_AndNotTheRedundantOnes()
    {
        var path = NewDatabase(out _);

        var executions = Indexes(path, "FlowExecutions");
        Assert.Contains("IX_FlowExecutions_Status_StartedAt", executions);
        Assert.Contains("IX_FlowExecutions_StartedAt", executions);
        Assert.Contains("IX_FlowExecutions_FlowId_StartedAt", executions);
        Assert.DoesNotContain("IX_FlowExecutions_FlowId", executions);

        var deadLetters = Indexes(path, "DeadLetterEntries");
        Assert.Contains("IX_DeadLetterEntries_Status_CreatedAt", deadLetters);
        Assert.Contains("IX_DeadLetterEntries_ReplayRequested", deadLetters);

        Assert.DoesNotContain("IX_StepPacketLogs_StepExecutionId", Indexes(path, "StepPacketLogs"));

        var logs = Indexes(path, "AppLogEntries");
        foreach (var dropped in new[] { "Service", "Outcome", "EventName", "Level" })
            Assert.DoesNotContain($"IX_AppLogEntries_{dropped}", logs);
        Assert.Contains("IX_AppLogEntries_Timestamp", logs);
        Assert.Contains("IX_AppLogEntries_FlowExecutionId", logs);
    }

    [Fact]
    public void ExistingDatabase_GainsTheIndexes_AndLosesTheRedundantOnes_OnTheNextStart()
    {
        var path = NewDatabase(out var options);

        // Put the database back into the shape it had before this change.
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX IX_FlowExecutions_Status_StartedAt;
                DROP INDEX IX_FlowExecutions_StartedAt;
                DROP INDEX IX_DeadLetterEntries_Status_CreatedAt;
                CREATE INDEX IX_FlowExecutions_FlowId ON FlowExecutions (FlowId);
                CREATE INDEX IX_AppLogEntries_Service ON AppLogEntries (Service);
                CREATE INDEX IX_AppLogEntries_Level ON AppLogEntries (Level);
                """;
            command.ExecuteNonQuery();
        }

        using (var context = new IPaaSContext(options))
            DatabaseSchemaInitializer.EnsureUpToDate(context);
        // A second pass must be a no-op rather than an error.
        using (var context = new IPaaSContext(options))
            DatabaseSchemaInitializer.EnsureUpToDate(context);

        var executions = Indexes(path, "FlowExecutions");
        Assert.Contains("IX_FlowExecutions_Status_StartedAt", executions);
        Assert.Contains("IX_FlowExecutions_StartedAt", executions);
        Assert.DoesNotContain("IX_FlowExecutions_FlowId", executions);
        Assert.Contains("IX_DeadLetterEntries_Status_CreatedAt", Indexes(path, "DeadLetterEntries"));
        var logs = Indexes(path, "AppLogEntries");
        Assert.DoesNotContain("IX_AppLogEntries_Service", logs);
        Assert.DoesNotContain("IX_AppLogEntries_Level", logs);
    }

    [Fact]
    public void HotQueries_UseTheirIndexes_InsteadOfScanningAndSorting()
    {
        var path = NewDatabase(out _);

        var claim = Plan(path, "SELECT Id FROM FlowExecutions WHERE Status = 1 ORDER BY COALESCE(QueuedAt, StartedAt) LIMIT 1");
        Assert.Contains("IX_FlowExecutions_Status_StartedAt", claim);

        var list = Plan(path, "SELECT * FROM FlowExecutions WHERE IsTest = 0 ORDER BY StartedAt DESC LIMIT 50");
        Assert.Contains("IX_FlowExecutions_StartedAt", list);
        Assert.DoesNotContain("TEMP B-TREE", list);

        var byFlow = Plan(path, "SELECT * FROM FlowExecutions WHERE FlowId = 'x' ORDER BY StartedAt DESC LIMIT 50");
        Assert.Contains("IX_FlowExecutions_FlowId_StartedAt", byFlow);

        var failed = Plan(path, "SELECT * FROM FlowExecutions WHERE Status = 3 ORDER BY StartedAt DESC LIMIT 50");
        Assert.Contains("IX_FlowExecutions_Status_StartedAt", failed);

        var manual = Plan(path, "SELECT * FROM DeadLetterEntries WHERE ReplayRequestedAt IS NOT NULL ORDER BY CreatedAt LIMIT 50");
        Assert.Contains("IX_DeadLetterEntries_ReplayRequested", manual);
        Assert.DoesNotContain("SCAN DeadLetterEntries", manual);

        var pending = Plan(path, "SELECT * FROM DeadLetterEntries WHERE Status = 'Pending' ORDER BY CreatedAt LIMIT 100");
        Assert.Contains("IX_DeadLetterEntries_Status_CreatedAt", pending);
        Assert.DoesNotContain("TEMP B-TREE", pending);
    }

    [Fact]
    public void TunedConnections_UseNormalSynchronousAndMemoryTempStore()
    {
        NewDatabase(out var options);
        using var context = new IPaaSContext(options);
        var connection = context.Database.GetDbConnection();
        connection.Open();

        Assert.Equal(1L, Scalar(connection, "PRAGMA synchronous;"));   // NORMAL
        Assert.Equal(2L, Scalar(connection, "PRAGMA temp_store;"));    // MEMORY
        Assert.Equal(8000L, Scalar(connection, "PRAGMA busy_timeout;"));
        Assert.Equal("wal", (string)Scalar(connection, "PRAGMA journal_mode;"));
    }

    private static object Scalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    [Fact]
    public async Task ReplayableDeadLetters_ManualFirst_ThenPending_SkipsTestRuns_AndHonoursTheBatchSize()
    {
        NewDatabase(out var options);
        var now = DateTime.UtcNow;
        Guid manualId;
        using (var context = new IPaaSContext(options))
        {
            var flow = new IntegrationFlow { Name = "Scan flow" };
            var step = new IntegrationStep { FlowId = flow.Id, NodeName = "Push", StepType = StepType.HttpAction };
            flow.Nodes.Add(step);
            context.IntegrationFlows.Add(flow);

            var live = new FlowExecution { FlowId = flow.Id, FlowName = flow.Name, TriggerSource = "Test", IsTest = false };
            var test = new FlowExecution { FlowId = flow.Id, FlowName = flow.Name, TriggerSource = "Test", IsTest = true };
            context.FlowExecutions.AddRange(live, test);

            DeadLetterEntry Entry(Guid runId, string status, DateTime created, DateTime? replay = null) => new()
            {
                FlowExecutionId = runId, StepId = step.Id, FlowName = flow.Name, NodeName = "Push",
                Status = status, CreatedAt = created, ReplayRequestedAt = replay,
            };

            for (var i = 0; i < 5; i++) context.DeadLetterEntries.Add(Entry(live.Id, "Pending", now.AddMinutes(-60 + i)));
            context.DeadLetterEntries.Add(Entry(live.Id, "Retried", now.AddMinutes(-90)));               // not replayable
            context.DeadLetterEntries.Add(Entry(test.Id, "Pending", now.AddMinutes(-80)));               // test run: skipped
            context.DeadLetterEntries.Add(Entry(test.Id, "Retrying", now.AddMinutes(-70), now));         // test run: skipped
            var manual = Entry(live.Id, "Retrying", now, now);
            manualId = manual.Id;
            context.DeadLetterEntries.Add(manual);
            await context.SaveChangesAsync();
        }

        using var scan = new IPaaSContext(options);
        var repository = new ExecutionRepository(scan);

        var all = await repository.GetReplayableDeadLettersAsync(100);
        Assert.Equal(6, all.Count);                          // 1 manual + 5 live pending, no Retried, no test runs
        Assert.Equal(manualId, all[0].Id);                   // manual request ahead of the older pending backlog
        Assert.Equal(all.Skip(1).Select(e => e.CreatedAt).OrderBy(c => c), all.Skip(1).Select(e => e.CreatedAt));

        var limited = await repository.GetReplayableDeadLettersAsync(3);
        Assert.Equal(3, limited.Count);
        Assert.Equal(manualId, limited[0].Id);

        var onlyManual = await repository.GetReplayableDeadLettersAsync(1);
        Assert.Equal(manualId, Assert.Single(onlyManual).Id);
    }

    [Fact]
    public async Task ExecutionList_DoesNotLoadTheLargePayloadColumns_ButTheDetailRowStillHasThem()
    {
        NewDatabase(out var options);
        var big = new string('p', 200_000);
        Guid id;
        using (var context = new IPaaSContext(options))
        {
            var flow = new IntegrationFlow { Name = "List flow" };
            context.IntegrationFlows.Add(flow);
            var run = new FlowExecution
            {
                FlowId = flow.Id, FlowName = flow.Name, IntegrationName = "Int", TriggerSource = "Webhook",
                Status = ExecutionStatus.Failed, ErrorMessage = "boom", Identifier = "ORD-1", Message = "msg",
                TotalRecords = 3, SuccessRecords = 2, FailedRecords = 1,
                TriggerPayloadJson = big, InitialFlowStateJson = big, InitialPersistedStateJson = big,
                FanOutItemJson = big, TestDefinitionJson = big, StartStepIdsJson = "[]"
            };
            id = run.Id;
            context.FlowExecutions.Add(run);
            await context.SaveChangesAsync();
        }

        using var read = new IPaaSContext(options);
        var repository = new ExecutionRepository(read);
        var listed = Assert.Single(await repository.GetFlowExecutionsAsync(new ExecutionQuery { IncludeTestRuns = true }));

        Assert.Equal(id, listed.Id);
        Assert.Equal("List flow", listed.FlowName);
        Assert.Equal(ExecutionStatus.Failed, listed.Status);
        Assert.Equal("boom", listed.ErrorMessage);
        Assert.Equal("ORD-1", listed.Identifier);
        Assert.Equal(2, listed.SuccessRecords);
        Assert.Null(listed.TriggerPayloadJson);
        Assert.Null(listed.InitialFlowStateJson);
        Assert.Null(listed.InitialPersistedStateJson);
        Assert.Null(listed.FanOutItemJson);
        Assert.Null(listed.TestDefinitionJson);
        Assert.Empty(read.ChangeTracker.Entries());   // nothing tracked for a read-only list

        var detail = await repository.GetFlowExecutionAsync(id);
        Assert.Equal(big, detail!.TriggerPayloadJson);
        Assert.Equal(big, detail.InitialFlowStateJson);
    }

    // ---- log facets cache ----

    [Fact]
    public async Task LogFacets_AreServedFromAShortCache_ForTheSameFilter()
    {
        var repository = new CountingFacetsRepository();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new LogsController(repository, null, null, cache);

        var first = Assert.IsType<OkObjectResult>(await controller.Facets());
        var second = Assert.IsType<OkObjectResult>(await controller.Facets());
        Assert.Equal(1, repository.FacetCalls);
        Assert.Equal(new[] { "Engine" }, ((LogFacetsDto)first.Value!).Services);
        Assert.Same(first.Value, second.Value);

        await controller.Facets(service: "Engine");   // a different filter is a different cache entry
        Assert.Equal(2, repository.FacetCalls);
    }

    [Fact]
    public async Task LogFacets_WithoutACache_StillQueryEveryTime()
    {
        var repository = new CountingFacetsRepository();
        var controller = new LogsController(repository);
        await controller.Facets();
        await controller.Facets();
        Assert.Equal(2, repository.FacetCalls);
    }

    private sealed class CountingFacetsRepository : ILogRepository
    {
        public int FacetCalls;

        public Task<LogFacets> GetFacetsAsync(LogQuery query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref FacetCalls);
            return Task.FromResult(new LogFacets(new[] { "Engine" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
        }

        public Task<LogPage> GetPageAsync(LogQuery query, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LogTail> GetTailAsync(long afterId, LogQuery query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppLogEntry>> GetExecutionTimelineAsync(Guid executionId, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TelemetryHealthSnapshot> GetTelemetryHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteAsync(DateTime? olderThanUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---- precompressed static files ----

    private IFileProvider WebRootWith(params string[] files)
    {
        var root = Path.Combine(_directory, "wwwroot");
        foreach (var file in files)
        {
            var full = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
        return new PhysicalFileProvider(root);
    }

    private static async Task<(PathString Path, string? Encoding)> RunStaticMiddleware(IFileProvider webRoot, string path, string acceptEncoding)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        if (acceptEncoding.Length > 0) context.Request.Headers.AcceptEncoding = acceptEncoding;

        PathString seen = default;
        var app = new ApplicationBuilder(new ServiceCollectionProvider());
        app.UsePrecompressedStaticFiles(webRoot);
        app.Run(c => { seen = c.Request.Path; return Task.CompletedTask; });
        await app.Build()(context);
        string? encoding = context.Response.Headers.ContentEncoding.Count == 0 ? null : context.Response.Headers.ContentEncoding.ToString();
        return (seen, encoding);
    }

    private sealed class ServiceCollectionProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public async Task Static_PrefersBrotli_ThenGzip_ThenTheOriginal()
    {
        var root = WebRootWith("_framework/app.wasm", "_framework/app.wasm.br", "_framework/app.wasm.gz", "only-gz.js", "only-gz.js.gz", "plain.css");

        var br = await RunStaticMiddleware(root, "/_framework/app.wasm", "gzip, deflate, br");
        Assert.Equal("/_framework/app.wasm.br", br.Path.Value);
        Assert.Equal("br", br.Encoding);

        var gz = await RunStaticMiddleware(root, "/_framework/app.wasm", "gzip");
        Assert.Equal("/_framework/app.wasm.gz", gz.Path.Value);
        Assert.Equal("gzip", gz.Encoding);

        var gzFallback = await RunStaticMiddleware(root, "/only-gz.js", "br, gzip");
        Assert.Equal("/only-gz.js.gz", gzFallback.Path.Value);

        var none = await RunStaticMiddleware(root, "/_framework/app.wasm", "");
        Assert.Equal("/_framework/app.wasm", none.Path.Value);
        Assert.Null(none.Encoding);

        var noTwin = await RunStaticMiddleware(root, "/plain.css", "br, gzip");
        Assert.Equal("/plain.css", noTwin.Path.Value);
        Assert.Null(noTwin.Encoding);

        var api = await RunStaticMiddleware(root, "/api/flows", "br");
        Assert.Equal("/api/flows", api.Path.Value);
        Assert.Null(api.Encoding);
    }

    [Fact]
    public void PrecompressedContentTypes_ResolveToTheOriginalType()
    {
        var provider = new PrecompressedContentTypeProvider(new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider());
        Assert.True(provider.TryGetContentType("/_framework/app.wasm.br", out var wasm));
        Assert.Equal("application/wasm", wasm);
        Assert.True(provider.TryGetContentType("/site.css.gz", out var css));
        Assert.Equal("text/css; charset=utf-8", css);   // UTF-8 stated, or browsers decode CSS glyphs as Windows-1252
        Assert.True(provider.TryGetContentType("/index.html", out var html));
        Assert.Equal("text/html; charset=utf-8", html);
    }

    // ---- page visibility ----

    [Fact]
    public void PageVisibility_DefaultsToVisible_AndRaisesChangedOnlyOnTransitions()
    {
        var service = new PageVisibilityService(new NullJsRuntime());
        var changes = new List<bool>();
        service.Changed += changes.Add;

        Assert.True(service.IsVisible);
        service.SetVisible(true);          // no transition
        service.SetVisible(false);
        service.SetVisible(false);         // no transition
        service.SetVisible(true);

        Assert.Equal(new[] { false, true }, changes);
        Assert.True(service.IsVisible);
    }

    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }
}
