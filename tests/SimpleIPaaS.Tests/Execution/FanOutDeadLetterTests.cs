using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;

namespace SimpleIPaaS.Tests.Execution;

// A failed Fan Out child's dead letter stores only what differs from the shared snapshot, and the snapshot lives as
// long as any child uses it.
public sealed class FanOutDeadLetterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public FanOutDeadLetterTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(_connection).Options;
        using var context = new IPaaSContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static readonly string BigOrders = "[" + string.Join(",", Enumerable.Range(1, 200).Select(i => $"{{\"id\":{i},\"note\":\"{new string('x', 1000)}\"}}")) + "]";

    // The parent's state when the Fan Out ran: ~200 KB of orders, a lookup table and a timestamp string.
    private static readonly string SnapshotState =
        $"{{\"trigger\":{{\"orders\":{BigOrders}}},\"Lookup\":{{\"currency\":\"EUR\"}},\"stamp\":\"2026-11-25T10:00:00.1234567+05:00\"}}";

    // A failing child: the snapshot plus its item (under the item variable and the card name) and one card's output.
    private static string ChildState()
    {
        var state = (JObject)JToken.ReadFrom(new Newtonsoft.Json.JsonTextReader(new StringReader(SnapshotState)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None });
        state["order"] = JObject.Parse("{\"id\":17}");
        state["Split"] = JObject.Parse("{\"id\":17}");
        state["MapOrder"] = JObject.Parse("{\"customer\":\"C-17\",\"total\":12.5}");
        return state.ToString(Newtonsoft.Json.Formatting.None);
    }

    [Fact]
    public void The_difference_keeps_only_added_changed_and_removed_keys_and_rebuilds_the_state_exactly()
    {
        var full = JObject.Parse("{\"trigger\":{\"a\":1},\"stamp\":\"2026-11-25T10:00:00+05:00\",\"Changed\":2,\"Added\":[1,2]}");
        var snapshot = "{\"trigger\":{\"a\":1},\"stamp\":\"2026-11-25T10:00:00+05:00\",\"Changed\":1,\"Gone\":true}";
        var fullJson = "{\"trigger\":{\"a\":1},\"stamp\":\"2026-11-25T10:00:00+05:00\",\"Changed\":2,\"Added\":[1,2]}";

        var difference = FanOutState.Diff(snapshot, fullJson)!.Value;

        Assert.True(JToken.DeepEquals(JObject.Parse("{\"Changed\":2,\"Added\":[1,2]}"), JObject.Parse(difference.ChangedJson)));
        Assert.Equal("[\"Gone\"]", difference.RemovedKeysJson);
        var rebuilt = FanOutState.Apply(snapshot, difference.ChangedJson, difference.RemovedKeysJson);
        Assert.True(JToken.DeepEquals(full, JObject.Parse(rebuilt)));
        // A date string a card never touched comes back exactly as it was stored.
        Assert.Contains("\"stamp\":\"2026-11-25T10:00:00+05:00\"", rebuilt);

        // Not an object: stored in full.
        Assert.Null(FanOutState.Diff(snapshot, "[1,2]"));
        Assert.Null(FanOutState.Diff(snapshot, "not json"));
    }

    [Fact]
    public async Task A_failed_child_stores_only_its_difference_and_every_reader_sees_the_full_state()
    {
        var (stepId, snapshotId, childId) = await SeedFanOutAsync();
        var full = ChildState();

        var entry = new DeadLetterEntry
        {
            FlowExecutionId = childId, StepId = stepId, NodeName = "PostToBC", FlowStateJson = full,
            FanOutSnapshotId = snapshotId, ErrorMessage = "502 from upstream",
        };
        await using (var context = new IPaaSContext(_options))
        {
            var repository = new ExecutionRepository(context);
            await repository.AddDeadLetterEntryAsync(entry);
            Assert.Equal(full, entry.FlowStateJson); // the caller keeps the full state
            await context.SaveChangesAsync();        // a later save in the same context does not write it to the row
        }

        var stored = await StoredAsync(entry.Id);
        Assert.Equal(snapshotId, stored.FanOutSnapshotId);
        Assert.True(stored.FlowStateJson.Length < 1_000, $"stored {stored.FlowStateJson.Length:N0} characters");
        Assert.True(full.Length > 200_000);
        Assert.Equal("[]", stored.RemovedStateKeysJson);

        // Read, change the status and save, as the replay path does: the row keeps the difference.
        await using (var context = new IPaaSContext(_options))
        {
            var repository = new ExecutionRepository(context);
            var loaded = (await repository.GetDeadLetterAsync(entry.Id))!;
            Assert.True(JToken.DeepEquals(JObject.Parse(full), JObject.Parse(loaded.FlowStateJson)));
            Assert.Contains("\"stamp\":\"2026-11-25T10:00:00.1234567+05:00\"", loaded.FlowStateJson);
            await context.SaveChangesAsync();
            loaded.Status = "Retrying";
            loaded.RetryCount++;
            await repository.UpdateDeadLetterEntryAsync(loaded);
        }

        stored = await StoredAsync(entry.Id);
        Assert.Equal("Retrying", stored.Status);
        Assert.True(stored.FlowStateJson.Length < 1_000, $"stored {stored.FlowStateJson.Length:N0} characters after an update");
    }

    [Fact]
    public async Task An_edited_state_is_stored_as_a_difference_too_including_changed_and_deleted_snapshot_keys()
    {
        var (stepId, snapshotId, childId) = await SeedFanOutAsync();
        var entry = new DeadLetterEntry
        {
            FlowExecutionId = childId, StepId = stepId, NodeName = "PostToBC", FlowStateJson = ChildState(), FanOutSnapshotId = snapshotId,
        };
        await using (var context = new IPaaSContext(_options))
        {
            await new ExecutionRepository(context).AddDeadLetterEntryAsync(entry);
        }

        // The operator fixes the currency (a snapshot key), deletes the timestamp (a snapshot key) and the card output.
        var edited = JObject.Parse(ChildState());
        edited["Lookup"] = JObject.Parse("{\"currency\":\"USD\"}");
        edited.Remove("stamp");
        edited.Remove("MapOrder");
        var editedJson = edited.ToString(Newtonsoft.Json.Formatting.None);
        await using (var context = new IPaaSContext(_options))
        {
            Assert.True(await new ExecutionRepository(context).TryRequestManualReplayAsync(entry.Id, stepId, DateTime.UtcNow, editedJson));
        }

        var stored = await StoredAsync(entry.Id);
        Assert.Equal(snapshotId, stored.FanOutSnapshotId);
        Assert.True(stored.FlowStateJson.Length < 1_000, $"stored {stored.FlowStateJson.Length:N0} characters");
        Assert.Equal("[\"stamp\"]", stored.RemovedStateKeysJson);

        await using (var context = new IPaaSContext(_options))
        {
            var loaded = (await new ExecutionRepository(context).GetDeadLetterAsync(entry.Id))!;
            Assert.True(JToken.DeepEquals(edited, JObject.Parse(loaded.FlowStateJson)));
            Assert.Null(JObject.Parse(loaded.FlowStateJson)["stamp"]);
            Assert.Null(JObject.Parse(loaded.FlowStateJson)["MapOrder"]);
        }
    }

    [Fact]
    public async Task An_entry_whose_snapshot_is_gone_or_that_is_not_a_child_is_stored_in_full()
    {
        var (stepId, _, childId) = await SeedFanOutAsync();
        var full = ChildState();
        var orphan = new DeadLetterEntry { FlowExecutionId = childId, StepId = stepId, FlowStateJson = full, FanOutSnapshotId = Guid.NewGuid() };
        var plain = new DeadLetterEntry { FlowExecutionId = childId, StepId = stepId, FlowStateJson = full };
        await using (var context = new IPaaSContext(_options))
        {
            var repository = new ExecutionRepository(context);
            await repository.AddDeadLetterEntryAsync(orphan);
            await repository.AddDeadLetterEntryAsync(plain);
        }

        foreach (var id in new[] { orphan.Id, plain.Id })
        {
            var stored = await StoredAsync(id);
            Assert.Null(stored.FanOutSnapshotId);
            Assert.Equal(full, stored.FlowStateJson);
        }
    }

    [Fact]
    public async Task Retention_keeps_a_fan_out_parent_until_no_child_uses_its_snapshot()
    {
        var (_, snapshotId, childId) = await SeedFanOutAsync(parentCompletedDaysAgo: 40, childCompletedDaysAgo: 1);
        Guid parentId;
        await using (var context = new IPaaSContext(_options))
        {
            parentId = (await context.FanOutSnapshots.SingleAsync()).ParentExecutionId;
        }

        await RunRetentionAsync();
        await using (var context = new IPaaSContext(_options))
        {
            // The parent is past the window, but its child still starts from (and rebuilds its dead letters from) the snapshot.
            Assert.True(await context.FlowExecutions.AnyAsync(run => run.Id == parentId));
            Assert.True(await context.FanOutSnapshots.AnyAsync(snapshot => snapshot.Id == snapshotId));
            Assert.True(await context.FlowExecutions.AnyAsync(run => run.Id == childId));

            var child = await context.FlowExecutions.SingleAsync(run => run.Id == childId);
            child.CompletedAt = DateTime.UtcNow.AddDays(-40);
            await context.SaveChangesAsync();
        }

        // The child goes first; the parent and its snapshot on the next pass at the latest.
        await RunRetentionAsync();
        await RunRetentionAsync();
        await using (var context = new IPaaSContext(_options))
        {
            Assert.False(await context.FlowExecutions.AnyAsync(run => run.Id == childId || run.Id == parentId));
            Assert.False(await context.FanOutSnapshots.AnyAsync());
        }
    }

    private async Task<(Guid StepId, Guid SnapshotId, Guid ChildId)> SeedFanOutAsync(int parentCompletedDaysAgo = 0, int childCompletedDaysAgo = 0)
    {
        var flow = new IntegrationFlow { Name = "Orders" };
        var step = new IntegrationStep { FlowId = flow.Id, NodeName = "PostToBC", StepType = StepType.HttpAction };
        var parent = new FlowExecution { FlowId = flow.Id, Status = ExecutionStatus.Success, CompletedAt = DateTime.UtcNow.AddDays(-parentCompletedDaysAgo) };
        var snapshot = new FanOutSnapshot { ParentExecutionId = parent.Id, ItemVariable = "order", NodeName = "Split", FlowStateJson = SnapshotState };
        var child = new FlowExecution
        {
            FlowId = flow.Id, ParentExecutionId = parent.Id, FanOutSnapshotId = snapshot.Id, FanOutItemJson = "{\"id\":17}",
            Status = ExecutionStatus.Failed, CompletedAt = DateTime.UtcNow.AddDays(-childCompletedDaysAgo),
        };
        await using var context = new IPaaSContext(_options);
        context.IntegrationFlows.Add(flow);
        context.IntegrationSteps.Add(step);
        context.FlowExecutions.AddRange(parent, child);
        context.FanOutSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
        return (step.Id, snapshot.Id, child.Id);
    }

    private async Task<DeadLetterEntry> StoredAsync(Guid id)
    {
        await using var context = new IPaaSContext(_options);
        return await context.DeadLetterEntries.AsNoTracking().SingleAsync(entry => entry.Id == id);
    }

    private async Task RunRetentionAsync()
    {
        var datasets = StorageCatalog.Defaults().Select(item =>
        {
            item.RetentionDays = item.Dataset == StorageDatasets.Executions ? 30 : 0;
            return item;
        }).ToList();
        var services = new ServiceCollection();
        services.AddScoped(_ => new IPaaSContext(_options));
        await using var provider = services.BuildServiceProvider();
        var retention = new StorageRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FixedSettings(StorageCatalog.Snapshot(new StorageSettings { DatasetsJson = StorageCatalog.Serialize(datasets) })),
            null!, null!, NullLogger<StorageRetentionService>.Instance);
        await retention.RunAsync();
    }

    private sealed class FixedSettings(StorageSettingsSnapshot snapshot) : IStorageSettingsProvider
    {
        public Task<StorageSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public bool TryGetCached(out StorageSettingsSnapshot cached) { cached = snapshot; return true; }
        public void Invalidate() { }
    }
}
