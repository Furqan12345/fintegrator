using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Logging;

public sealed class LogsEndpointTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public LogsEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(_connection)
            .Options;

        using var seed = CreateContext();
        seed.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options, new StubTenantContext(Tenant));

    private LogRepository CreateRepository(IPaaSContext context) => new(context);

    private long Seed(params AppLogEntry[] entries)
    {
        using var context = CreateContext();
        context.AppLogEntries.AddRange(entries);
        context.SaveChanges();
        return entries.Length == 0 ? 0 : entries.Max(entry => entry.Id);
    }

    private static AppLogEntry Entry(
        string level = "Information",
        string source = "Engine",
        string message = "hello",
        string category = "SimpleIPaaS.Engine.Workers.FlowExecutionWorker",
        Guid? executionId = null,
        DateTime? timestamp = null) => new()
        {
            Timestamp = timestamp ?? DateTime.UtcNow,
            Level = level,
            Source = source,
            Category = category,
            Message = message,
            FlowExecutionId = executionId
        };

    // ---------------- tail cursor ----------------

    [Fact]
    public async Task Tail_FromZero_ReturnsEverythingOldestFirstAndParksTheCursorOnTheLastRow()
    {
        Seed(Entry(message: "first"), Entry(message: "second"), Entry(message: "third"));

        using var context = CreateContext();
        var tail = await CreateRepository(context).GetTailAsync(0, new LogQuery(), 100);

        Assert.Equal(new[] { "first", "second", "third" }, tail.Entries.Select(entry => entry.Message).ToArray());
        Assert.Equal(tail.Entries[^1].Id, tail.Cursor);
        Assert.False(tail.Reset);
    }

    [Fact]
    public async Task Tail_AtTheCursor_ReturnsNothingAndLeavesTheCursorWhereItWas()
    {
        var lastId = Seed(Entry(message: "first"), Entry(message: "second"));

        using var context = CreateContext();
        var tail = await CreateRepository(context).GetTailAsync(lastId, new LogQuery(), 100);

        Assert.Empty(tail.Entries);
        Assert.Equal(lastId, tail.Cursor);
        Assert.False(tail.Reset);
    }

    [Fact]
    public async Task Tail_AdvancesAsNewEntriesArrive()
    {
        var firstCursor = Seed(Entry(message: "first"));

        Seed(Entry(message: "second"), Entry(message: "third"));

        using var context = CreateContext();
        var tail = await CreateRepository(context).GetTailAsync(firstCursor, new LogQuery(), 100);

        Assert.Equal(new[] { "second", "third" }, tail.Entries.Select(entry => entry.Message).ToArray());
        Assert.True(tail.Cursor > firstCursor);
        Assert.Equal(tail.Entries[^1].Id, tail.Cursor);
    }

    [Fact]
    public async Task Tail_RespectsTheLimitAndOnlyAdvancesAsFarAsItDelivered()
    {
        Seed(Enumerable.Range(0, 10).Select(i => Entry(message: $"entry {i}")).ToArray());

        using var context = CreateContext();
        var repository = CreateRepository(context);

        var first = await repository.GetTailAsync(0, new LogQuery(), 4);
        Assert.Equal(4, first.Entries.Count);
        Assert.Equal(first.Entries[^1].Id, first.Cursor);

        // Nothing is skipped: resuming from the cursor picks up exactly where it stopped.
        var second = await repository.GetTailAsync(first.Cursor, new LogQuery(), 4);
        Assert.Equal("entry 4", second.Entries[0].Message);
    }

    [Fact]
    public async Task Tail_WithACursorAheadOfTheTable_SignalsResetInsteadOfStallingForever()
    {
        Seed(Entry(message: "after the clear"));

        using var context = CreateContext();
        var tail = await CreateRepository(context).GetTailAsync(9_999, new LogQuery(), 100);

        Assert.True(tail.Reset);
        Assert.Single(tail.Entries);
        Assert.Equal("after the clear", tail.Entries[0].Message);
        Assert.Equal(tail.Entries[0].Id, tail.Cursor);
    }

    [Fact]
    public async Task Tail_AppliesTheSameFiltersAsThePagedListing()
    {
        Seed(
            Entry(level: "Information", source: "Engine", message: "engine info"),
            Entry(level: "Error", source: "Engine", message: "engine error"),
            Entry(level: "Error", source: "Api", message: "api error"));

        using var context = CreateContext();
        var repository = CreateRepository(context);

        var errorsOnly = await repository.GetTailAsync(0, new LogQuery { MinimumLevel = "Warning" }, 100);
        Assert.Equal(new[] { "engine error", "api error" }, errorsOnly.Entries.Select(e => e.Message).ToArray());

        var engineOnly = await repository.GetTailAsync(0, new LogQuery { Source = "Engine" }, 100);
        Assert.Equal(2, engineOnly.Entries.Count);
    }

    // ---------------- filters ----------------

    [Fact]
    public async Task MinimumLevel_IncludesEverythingAtOrAboveTheGivenSeverity()
    {
        Seed(
            Entry(level: "Trace"),
            Entry(level: "Debug"),
            Entry(level: "Information"),
            Entry(level: "Warning"),
            Entry(level: "Error"),
            Entry(level: "Critical"));

        using var context = CreateContext();
        var repository = CreateRepository(context);

        Assert.Equal(6, (await repository.GetPageAsync(new LogQuery(), 1, 100)).Total);
        Assert.Equal(4, (await repository.GetPageAsync(new LogQuery { MinimumLevel = "Information" }, 1, 100)).Total);
        Assert.Equal(2, (await repository.GetPageAsync(new LogQuery { MinimumLevel = "error" }, 1, 100)).Total);
        Assert.Equal(1, (await repository.GetPageAsync(new LogQuery { MinimumLevel = "Critical" }, 1, 100)).Total);
    }

    [Fact]
    public async Task Search_MatchesMessageCategoryAndException()
    {
        using (var context = CreateContext())
        {
            context.AppLogEntries.AddRange(
                Entry(message: "Claimed queued execution"),
                Entry(message: "unrelated", category: "SimpleIPaaS.Engine.Workers.CronTriggerScheduler"),
                new AppLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Source = "Engine",
                    Category = "Worker",
                    Message = "boom",
                    Exception = "System.Net.Http.HttpRequestException: connection refused"
                });
            context.SaveChanges();
        }

        using var readContext = CreateContext();
        var repository = CreateRepository(readContext);

        Assert.Equal(1, (await repository.GetPageAsync(new LogQuery { Search = "queued" }, 1, 50)).Total);
        Assert.Equal(1, (await repository.GetPageAsync(new LogQuery { Search = "CronTrigger" }, 1, 50)).Total);
        Assert.Equal(1, (await repository.GetPageAsync(new LogQuery { Search = "connection refused" }, 1, 50)).Total);
        Assert.Equal(0, (await repository.GetPageAsync(new LogQuery { Search = "nothing matches this" }, 1, 50)).Total);
    }

    [Fact]
    public async Task Search_TreatsWildcardCharactersLiterally()
    {
        Seed(Entry(message: "100% complete"), Entry(message: "10 complete"));

        using var context = CreateContext();
        var result = await CreateRepository(context).GetPageAsync(new LogQuery { Search = "100%" }, 1, 50);

        Assert.Equal(1, result.Total);
        Assert.Equal("100% complete", result.Items[0].Message);
    }

    [Fact]
    public async Task FlowExecutionId_CorrelatesASingleRun()
    {
        var executionId = Guid.NewGuid();
        Seed(
            Entry(message: "in the run", executionId: executionId),
            Entry(message: "also in the run", executionId: executionId),
            Entry(message: "another run", executionId: Guid.NewGuid()),
            Entry(message: "uncorrelated"));

        using var context = CreateContext();
        var result = await CreateRepository(context).GetPageAsync(new LogQuery { FlowExecutionId = executionId }, 1, 50);

        Assert.Equal(2, result.Total);
    }

    [Fact]
    public async Task Since_FiltersByTimestamp()
    {
        Seed(
            Entry(message: "old", timestamp: DateTime.UtcNow.AddHours(-5)),
            Entry(message: "new", timestamp: DateTime.UtcNow));

        using var context = CreateContext();
        var result = await CreateRepository(context)
            .GetPageAsync(new LogQuery { SinceUtc = DateTime.UtcNow.AddHours(-1) }, 1, 50);

        Assert.Equal(1, result.Total);
        Assert.Equal("new", result.Items[0].Message);
    }

    // ---------------- paging / sources / delete ----------------

    [Fact]
    public async Task Page_ReturnsNewestFirstAndCarriesTheTailCursorForTheWholeTable()
    {
        var newestId = Seed(Enumerable.Range(0, 12).Select(i => Entry(message: $"entry {i}")).ToArray());

        using var context = CreateContext();
        var page = await CreateRepository(context).GetPageAsync(new LogQuery(), 2, 5);

        Assert.Equal(12, page.Total);
        Assert.Equal(5, page.Items.Count);

        // Newest first: page 1 is entries 11..7, so page 2 starts at entry 6.
        Assert.Equal("entry 6", page.Items[0].Message);
        Assert.Equal("entry 2", page.Items[^1].Message);

        // The cursor is the newest row in the WHOLE table, not on this page, so a client can
        // page through history and still start tailing from "now" without gaps.
        Assert.Equal(newestId, page.Cursor);
    }

    [Fact]
    public async Task Sources_ListsTheDistinctHostsThatHaveLogged()
    {
        Seed(Entry(source: "Engine"), Entry(source: "Engine"), Entry(source: "Api"));

        using var context = CreateContext();
        var sources = await CreateRepository(context).GetSourcesAsync();

        Assert.Equal(new[] { "Api", "Engine" }, sources.ToArray());
    }

    [Fact]
    public async Task Delete_ClearsEverythingOrOnlyWhatIsOlderThanTheCutoff()
    {
        Seed(
            Entry(message: "old", timestamp: DateTime.UtcNow.AddHours(-10)),
            Entry(message: "new", timestamp: DateTime.UtcNow));

        using (var context = CreateContext())
        {
            var deleted = await CreateRepository(context).DeleteAsync(DateTime.UtcNow.AddHours(-1));
            Assert.Equal(1, deleted);
        }

        using (var context = CreateContext())
        {
            Assert.Equal(1, await context.AppLogEntries.CountAsync());
            Assert.Equal(1, await CreateRepository(context).DeleteAsync(null));
            Assert.Equal(0, await context.AppLogEntries.CountAsync());
        }
    }

    // ---------------- controller ----------------

    [Fact]
    public async Task TailEndpoint_ReturnsEntriesAndAStableCursor()
    {
        Seed(Entry(message: "first"), Entry(message: "second"));

        using var context = CreateContext();
        var controller = new LogsController(CreateRepository(context));

        var first = Assert.IsType<LogTailDto>(Assert.IsType<OkObjectResult>(await controller.Tail()).Value);
        Assert.Equal(2, first.Entries.Count);
        Assert.False(first.Reset);

        // Polling again with the returned cursor yields nothing new and the same cursor.
        var second = Assert.IsType<LogTailDto>(
            Assert.IsType<OkObjectResult>(await controller.Tail(afterId: first.Cursor)).Value);
        Assert.Empty(second.Entries);
        Assert.Equal(first.Cursor, second.Cursor);

        Seed(Entry(message: "third"));

        var third = Assert.IsType<LogTailDto>(
            Assert.IsType<OkObjectResult>(await controller.Tail(afterId: second.Cursor)).Value);
        Assert.Equal("third", Assert.Single(third.Entries).Message);
        Assert.True(third.Cursor > second.Cursor);
    }

    [Fact]
    public async Task TailEndpoint_RejectsANegativeCursor()
    {
        using var context = CreateContext();
        var controller = new LogsController(CreateRepository(context))
        {
            ProblemDetailsFactory = new StubProblemDetailsFactory()
        };

        var result = await controller.Tail(afterId: -1);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.IsType<ValidationProblemDetails>(problem.Value);
    }

    [Fact]
    public async Task LogsEndpoint_RejectsAnUnknownLevel()
    {
        using var context = CreateContext();
        var controller = new LogsController(CreateRepository(context))
        {
            ProblemDetailsFactory = new StubProblemDetailsFactory()
        };

        var result = await controller.GetLogs(level: "Verbose");

        var problem = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task LogsEndpoint_ReturnsAFilteredPage()
    {
        Seed(Entry(level: "Information", message: "info"), Entry(level: "Error", message: "boom"));

        using var context = CreateContext();
        var controller = new LogsController(CreateRepository(context));

        var page = Assert.IsType<LogPageDto>(
            Assert.IsType<OkObjectResult>(await controller.GetLogs(level: "Error")).Value);

        Assert.Equal(1, page.Total);
        Assert.Equal("boom", Assert.Single(page.Items).Message);
        Assert.True(page.Cursor > 0);
    }

    [Fact]
    public void LevelsEndpoint_ListsEverySeverityInOrder()
    {
        using var context = CreateContext();
        var controller = new LogsController(CreateRepository(context));

        var levels = Assert.IsType<OkObjectResult>(controller.GetLevels()).Value as IReadOnlyList<string>;

        Assert.Equal(
            new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" },
            levels!.ToArray());
    }

    public void Dispose() => _connection.Dispose();
}
