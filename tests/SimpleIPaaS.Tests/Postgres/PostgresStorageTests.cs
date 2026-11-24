using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;

namespace SimpleIPaaS.Tests.Postgres;

// Runs only when FUSETOR_TEST_POSTGRES holds a connection string to a database the tests may wipe, e.g.
//   Host=localhost;Port=55432;Database=fusetor_test;Username=postgres;Password=...
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "FUSETOR_TEST_POSTGRES";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a disposable PostgreSQL database to run the PostgreSQL storage tests.";
        }
    }
}

// One schema for the whole class: wiped and rebuilt once, through the same start-up path the hosts use.
public sealed class PostgresDatabaseFixture : IDisposable
{
    public NpgsqlDataSource? DataSource { get; }

    public PostgresDatabaseFixture()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        DataSource = PostgresDataSource.Create(connectionString);
        using (var connection = DataSource.OpenConnection())
        using (var reset = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", connection))
        {
            reset.ExecuteNonQuery();
        }

        // Two hosts start at once in production: the advisory lock must make concurrent start-ups safe.
        Parallel.For(0, 2, _ =>
        {
            using var context = NewContext();
            DatabaseSchemaInitializer.EnsureUpToDate(context, NullLogger.Instance);
        });
    }

    public IPaaSContext NewContext() =>
        new(new DbContextOptionsBuilder<IPaaSContext>().UseNpgsql(DataSource!).Options);

    public void Dispose() => DataSource?.Dispose();
}

public sealed class PostgresStorageTests : IClassFixture<PostgresDatabaseFixture>
{
    private readonly PostgresDatabaseFixture _db;

    public PostgresStorageTests(PostgresDatabaseFixture db) => _db = db;

    [PostgresFact]
    public async Task Schema_partitions_the_append_only_tables_and_keeps_payloads_out_of_the_hot_tables()
    {
        await using var connection = await _db.DataSource!.OpenConnectionAsync();
        foreach (var table in PostgresLayout.PartitionedTables)
        {
            Assert.True(await PostgresPartitions.IsPartitionedAsync(connection, table.Name), table.Name);
            var partitions = await PostgresPartitions.ListAsync(connection, null, table.Name);
            var today = PostgresPartitions.UtcDay(DateTime.UtcNow);
            Assert.Contains(PostgresPartitions.PartitionName(table.Name, today), partitions);
            Assert.Contains(PostgresPartitions.PartitionName(table.Name, today.AddDays(PostgresPartitions.DaysAhead)), partitions);
        }

        var stepColumns = await ColumnsAsync(connection, "StepExecutions");
        Assert.DoesNotContain("ReceivedInput", stepColumns);
        Assert.DoesNotContain("ResponsePayload", stepColumns);
        Assert.Contains(PostgresLayout.ResponsePayloadLength, stepColumns);

        var packetColumns = await ColumnsAsync(connection, "StepPacketLogs");
        Assert.DoesNotContain("ResponseBody", packetColumns);
        Assert.Contains(PostgresLayout.ResponseBodyLength, packetColumns);
        Assert.Contains("ResponseBody", await ColumnsAsync(connection, "StepPacketPayloads"));
    }

    [PostgresFact]
    public async Task Step_payloads_round_trip_through_the_cold_table_and_summaries_read_only_hot_sizes()
    {
        var (flowId, nodeId) = await SeedFlowAsync("Order sync");
        var runId = Guid.NewGuid();
        var step = new StepExecution
        {
            FlowExecutionId = runId,
            StepId = nodeId,
            NodeName = "FetchOrders",
            ReceivedInput = "{\"page\":1}",
            StartedAt = DateTime.UtcNow,
        };

        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(new FlowExecution { Id = runId, FlowId = flowId, FlowName = "Order sync" });
            await repository.AddStepExecutionAsync(step);
            step.RequestPayload = "GET /orders";
            await repository.UpdateStepExecutionAsync(step);
            step.ResponsePayload = new string('x', 5000);
            step.Status = ExecutionStatus.Success;
            await repository.UpdateStepExecutionAsync(step);
        }

        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            var loaded = Assert.Single(await repository.GetStepExecutionsAsync(runId));
            Assert.Equal("{\"page\":1}", loaded.ReceivedInput);
            Assert.Equal("GET /orders", loaded.RequestPayload);
            Assert.Equal(5000, loaded.ResponsePayload.Length);

            var summary = Assert.Single(await repository.GetStepExecutionSummariesAsync(runId));
            Assert.Equal(10, summary.ReceivedInputSize);
            Assert.Equal(5000, summary.ResponsePayloadSize);

            var payload = await repository.GetStepPayloadAsync(runId, step.Id);
            Assert.Equal(5000, payload!.ResponsePayload.Length);

            // A step loaded in a new scope (dead-letter recovery does this) updates its existing cold row.
            var reloaded = await repository.GetStepExecutionAsync(runId, nodeId);
            reloaded!.ResponsePayload = "recovered";
            await repository.UpdateStepExecutionAsync(reloaded);
        }

        await using (var context = _db.NewContext())
        {
            Assert.Equal(1, await context.Set<StepExecutionPayload>().CountAsync(p => p.StepExecutionId == step.Id));
            Assert.Equal("recovered", (await new ExecutionRepository(context).GetStepPayloadAsync(runId, step.Id))!.ResponsePayload);
        }
    }

    [PostgresFact]
    public async Task Packet_burst_is_written_in_COPY_batches_and_bodies_are_read_from_the_cold_table()
    {
        var (flowId, nodeId) = await SeedFlowAsync("Inventory sync");
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(new FlowExecution { Id = runId, FlowId = flowId, FlowName = "Inventory sync" });
            await repository.AddStepExecutionAsync(new StepExecution { Id = stepId, FlowExecutionId = runId, StepId = nodeId, NodeName = "PushStock" });
        }

        var ingestor = new PostgresPacketIngestor(_db.DataSource!, NullLogger<PostgresPacketIngestor>.Instance);
        await using (var context = _db.NewContext())
        {
            var store = new PostgresPacketLogStore(context, ingestor);
            for (var i = 0; i < 1200; i++)
            {
                await store.AddAsync(new StepPacketLog
                {
                    StepExecutionId = stepId,
                    Sequence = i,
                    Kind = "HTTP",
                    HttpMethod = "POST",
                    RequestUrl = $"https://example.test/stock/{i}",
                    StatusCode = 200,
                    RequestBody = "{\"sku\":\"A-" + i + "\"}",
                    ResponseBody = i == 7 ? "binary\0tail" : new string('r', 300),
                    CompletedAt = DateTime.UtcNow,
                    DurationMs = 12,
                });
            }
        }

        await ingestor.FlushAsync();
        Assert.Equal(1200, ingestor.WrittenCount);
        Assert.True(ingestor.BatchCount >= 3, $"expected batches of at most {PostgresPacketIngestor.BatchSize}, got {ingestor.BatchCount}");
        Assert.Equal(0, ingestor.DroppedCount);

        await using (var context = _db.NewContext())
        {
            var store = new PostgresPacketLogStore(context, ingestor);
            var summaries = await store.GetSummariesAsync(runId);
            Assert.Equal(1200, summaries.Count);
            Assert.All(summaries, summary => Assert.True(summary.ResponseBodySize > 0));

            var seventh = summaries.Single(summary => summary.Sequence == 7);
            var body = await store.GetBodyAsync(runId, seventh.Id);
            Assert.Equal("binary�tail", body!.ResponseBody);

            var detail = await store.GetDetailAsync(seventh.Id);
            Assert.Equal("{\"sku\":\"A-7\"}", detail!.RequestBody);
            Assert.Equal("PushStock", detail.NodeName);

            var page = await store.GetPageAsync(new PacketLogPageFilter(FlowExecutionId: runId, PageSize: 50));
            Assert.Equal(50, page.Count);

            var full = await store.GetByStepExecutionAsync(stepId);
            Assert.Equal(1200, full.Count);
            Assert.Equal(300, full[0].ResponseBody.Length);
        }
    }

    [PostgresFact]
    public async Task Missing_day_partition_is_created_on_write_and_old_days_are_dropped_whole()
    {
        var (flowId, nodeId) = await SeedFlowAsync("Backfill");
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(new FlowExecution { Id = runId, FlowId = flowId, FlowName = "Backfill" });
            await repository.AddStepExecutionAsync(new StepExecution { Id = stepId, FlowExecutionId = runId, StepId = nodeId, NodeName = "Old" });
        }

        var oldDay = DateTime.UtcNow.Date.AddDays(-40).AddHours(3);
        var ingestor = new PostgresPacketIngestor(_db.DataSource!, NullLogger<PostgresPacketIngestor>.Instance);
        await ingestor.EnqueueAsync(new StepPacketLog { StepExecutionId = stepId, Kind = "HTTP", HttpMethod = "GET", StartedAt = oldDay });
        await ingestor.EnqueueAsync(new StepPacketLog { StepExecutionId = stepId, Kind = "HTTP", HttpMethod = "GET", Sequence = 1 });
        await ingestor.FlushAsync();
        Assert.Equal(2, ingestor.WrittenCount);

        var oldPartition = PostgresPartitions.PartitionName(PostgresLayout.StepPacketLogs, PostgresPartitions.UtcDay(oldDay));
        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        {
            Assert.Contains(oldPartition, await PostgresPartitions.ListAsync(connection, null, PostgresLayout.StepPacketLogs));
        }

        var dropped = await PostgresPartitions.DropOlderThanAsync(_db.DataSource!, PostgresLayout.StepPacketLogs, DateTime.UtcNow.AddDays(-30));
        Assert.True(dropped.Partitions >= 1);
        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        {
            var remaining = await PostgresPartitions.ListAsync(connection, null, PostgresLayout.StepPacketLogs);
            Assert.DoesNotContain(oldPartition, remaining);
            Assert.Contains(PostgresPartitions.PartitionName(PostgresLayout.StepPacketLogs, PostgresPartitions.UtcDay(DateTime.UtcNow)), remaining);
        }

        await using (var context = _db.NewContext())
        {
            Assert.Equal(1, await context.StepPacketLogs.CountAsync(packet => packet.StepExecutionId == stepId));
        }
    }

    [PostgresFact]
    public async Task Log_batches_are_copied_and_search_is_case_insensitive()
    {
        var store = new PostgresAppLogBatchStore(() => _db.DataSource!);
        var marker = "Marker" + Guid.NewGuid().ToString("N")[..8];
        var batch = Enumerable.Range(0, 300).Select(i => new AppLogEntry
        {
            Level = i % 10 == 0 ? "Warning" : "Information",
            Source = "Engine",
            Category = "Tests",
            Message = $"{marker} entry {i}",
            FlowId = Guid.NewGuid(),
            IsTest = true,
        }).ToList();
        store.Write(batch);

        await using var context = _db.NewContext();
        Assert.Equal(300, await context.AppLogEntries.CountAsync(entry => entry.Message.StartsWith(marker)));

        var repository = new LogRepository(context);
        var page = await repository.GetPageAsync(new LogQuery { Search = marker.ToUpperInvariant() }, 1, 500);
        Assert.Equal(300, page.Total);

        // Retention: nothing here is old enough to go; the row cap trims to the newest rows.
        store.Prune(new LogRetentionWindows(72, 168, 100));
        Assert.True(await context.AppLogEntries.CountAsync() <= 100);
    }

    [PostgresFact]
    public async Task Run_search_is_case_insensitive_and_non_utc_datetimes_are_accepted()
    {
        var (flowId, _) = await SeedFlowAsync("Shopify Orders");
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(new FlowExecution
            {
                FlowId = flowId,
                FlowName = "Shopify Orders",
                Identifier = "ORDER-1001",
                // timestamptz only accepts UTC; the model normalises other kinds instead of failing the write.
                StartedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
                QueuedAt = DateTime.Now,
            });
        }

        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            var found = (await repository.GetFlowExecutionsAsync(new ExecutionQuery { Search = "order-1001", PageSize = 10 })).ToList();
            var run = Assert.Single(found);
            Assert.Equal(DateTimeKind.Utc, run.StartedAt.Kind);
        }
    }

    [PostgresFact]
    public async Task Deleting_a_flow_removes_its_cold_rows()
    {
        var (flowId, nodeId) = await SeedFlowAsync("Disposable");
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(new FlowExecution { Id = runId, FlowId = flowId, FlowName = "Disposable" });
            await repository.AddStepExecutionAsync(new StepExecution { Id = stepId, FlowExecutionId = runId, StepId = nodeId, ReceivedInput = "x" });
        }

        var ingestor = new PostgresPacketIngestor(_db.DataSource!, NullLogger<PostgresPacketIngestor>.Instance);
        await ingestor.EnqueueAsync(new StepPacketLog { StepExecutionId = stepId, Kind = "HTTP", ResponseBody = "y" });
        await ingestor.FlushAsync();

        await using (var context = _db.NewContext())
        {
            await new IntegrationRepository(context).DeleteAsync(flowId);
        }

        await using (var verify = _db.NewContext())
        {
            Assert.Equal(0, await verify.Set<StepExecutionPayload>().CountAsync(p => p.StepExecutionId == stepId));
            Assert.Equal(0, await verify.StepPacketLogs.CountAsync(p => p.StepExecutionId == stepId));
            Assert.Equal(0, await verify.FlowExecutions.CountAsync(run => run.Id == runId));
        }
    }

    [PostgresFact]
    public async Task Run_retention_deletes_the_runs_packet_logs_and_payloads_too()
    {
        var (flowId, nodeId) = await SeedFlowAsync("Retention");
        var oldRun = Guid.NewGuid();
        var oldStep = Guid.NewGuid();
        var keptRun = Guid.NewGuid();
        var keptStep = Guid.NewGuid();
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            foreach (var (run, step) in new[] { (oldRun, oldStep), (keptRun, keptStep) })
            {
                await repository.AddFlowExecutionAsync(new FlowExecution { Id = run, FlowId = flowId, FlowName = "Retention", Status = ExecutionStatus.Success, CompletedAt = DateTime.UtcNow });
                await repository.AddStepExecutionAsync(new StepExecution { Id = step, FlowExecutionId = run, StepId = nodeId, ReceivedInput = "in" });
            }
        }

        var ingestor = new PostgresPacketIngestor(_db.DataSource!, NullLogger<PostgresPacketIngestor>.Instance);
        await ingestor.EnqueueAsync(new StepPacketLog { StepExecutionId = oldStep, Kind = "HTTP", ResponseBody = "old" });
        await ingestor.EnqueueAsync(new StepPacketLog { StepExecutionId = keptStep, Kind = "HTTP", ResponseBody = "kept" });
        await ingestor.FlushAsync();

        await using (var context = _db.NewContext())
        {
            Assert.Equal(1, await StorageRetentionService.DeletePostgresRunsAsync(context, new List<Guid> { oldRun }, default));
        }

        await using var verify = _db.NewContext();
        // No foreign key reaches the packet tables on PostgreSQL: without the explicit delete these would remain.
        Assert.Equal(0, await verify.StepPacketLogs.CountAsync(packet => packet.StepExecutionId == oldStep));
        Assert.Equal(0, await verify.Set<StepPacketPayload>().CountAsync(payload => payload.ResponseBody == "old"));
        Assert.Equal(0, await verify.Set<StepExecutionPayload>().CountAsync(payload => payload.StepExecutionId == oldStep));
        Assert.Equal(0, await verify.StepExecutions.CountAsync(step => step.Id == oldStep));
        Assert.Equal(1, await verify.StepPacketLogs.CountAsync(packet => packet.StepExecutionId == keptStep));
        Assert.Equal(1, await verify.Set<StepPacketPayload>().CountAsync(payload => payload.ResponseBody == "kept"));
        Assert.Equal(1, await verify.FlowExecutions.CountAsync(run => run.Id == keptRun));
    }

    [PostgresFact]
    public async Task Start_up_creates_a_table_an_older_database_is_missing_and_fan_out_snapshots_round_trip()
    {
        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand("DROP TABLE \"FanOutSnapshots\";", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using (var context = _db.NewContext())
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context, NullLogger.Instance);
        }

        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        await using (var check = new NpgsqlCommand(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = to_regclass('\"FanOutSnapshots\"') AND contype = 'f';", connection))
        {
            Assert.Equal(1L, await check.ExecuteScalarAsync());
        }

        var (flowId, _) = await SeedFlowAsync("Fan out");
        var parentId = Guid.NewGuid();
        var snapshot = new FanOutSnapshot { ParentExecutionId = parentId, FanOutNodeId = Guid.NewGuid(), ItemVariable = "order", NodeName = "Split", FlowStateJson = "{\"big\":[1,2,3]}" };
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(new FlowExecution { Id = parentId, FlowId = flowId, FlowName = "Fan out" });
            await repository.AddFanOutAsync(snapshot, new[]
            {
                new FlowExecution { FlowId = flowId, ParentExecutionId = parentId, FanOutSnapshotId = snapshot.Id, FanOutItemJson = "{\"id\":7}", Status = ExecutionStatus.Queued },
            });
        }

        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            // Loaded the way the Engine loads it: the item lives in the run's cold payload row.
            var childId = await context.FlowExecutions.Where(run => run.ParentExecutionId == parentId).Select(run => run.Id).SingleAsync();
            var child = (await repository.GetFlowExecutionAsync(childId))!;
            var state = Newtonsoft.Json.Linq.JObject.Parse((await Application.Services.FanOutState.ResolveInitialFlowStateAsync(child, repository))!);
            Assert.Equal(7, (int)state["order"]!["id"]!);
            Assert.Equal(3, state["big"]!.Count());

            // The snapshot goes with its parent run.
            await context.FlowExecutions.Where(run => run.ParentExecutionId == parentId).ExecuteDeleteAsync();
            await context.FlowExecutions.Where(run => run.Id == parentId).ExecuteDeleteAsync();
            Assert.Null(await repository.GetFanOutSnapshotAsync(snapshot.Id));
        }
    }

    [PostgresFact]
    public async Task Run_and_dead_letter_payloads_live_in_cold_tables_and_status_updates_never_rewrite_them()
    {
        var (flowId, nodeId) = await SeedFlowAsync("Webhook orders");
        var body = "{\"order\":\"" + new string('w', 200_000) + "\"}";
        var run = new FlowExecution { FlowId = flowId, FlowName = "Webhook orders", Status = ExecutionStatus.Queued, TriggerSource = "Webhook", TriggerPayloadJson = body, QueuedAt = DateTime.UtcNow.AddYears(-1) };
        var deadLetter = new DeadLetterEntry { FlowExecutionId = run.Id, StepId = nodeId, Payload = "{\"p\":1}", FlowStateJson = "{\"s\":1}", PersistedStateJson = "{\"w\":1}" };
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(run);
            await repository.AddDeadLetterEntryAsync(deadLetter);
        }

        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        {
            foreach (var (table, column) in new[] { ("FlowExecutions", "TriggerPayloadJson"), ("DeadLetterEntries", "FlowStateJson") })
            {
                await using var hot = new NpgsqlCommand(
                    "SELECT count(*) FROM information_schema.columns WHERE table_name = @t AND column_name = @c;", connection);
                hot.Parameters.AddWithValue("t", table);
                hot.Parameters.AddWithValue("c", column);
                Assert.Equal(0L, await hot.ExecuteScalarAsync());
            }
        }

        string? xminBefore;
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            var claim = await repository.TryClaimNextQueuedExecutionAsync();
            Assert.Equal(run.Id, claim!.ExecutionId);
            Assert.Equal(body, claim.TriggerPayload);

            xminBefore = await XminAsync(run.Id);
            var loaded = await repository.GetFlowExecutionAsync(run.Id);
            Assert.Equal(body, loaded!.TriggerPayloadJson);
            loaded.Status = ExecutionStatus.Success;
            loaded.CompletedAt = DateTime.UtcNow;
            await repository.UpdateFlowExecutionAsync(loaded);
        }

        // The cold row was not touched by the status update (same row version).
        Assert.Equal(xminBefore, await XminAsync(run.Id));

        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            var entry = await repository.GetDeadLetterAsync(deadLetter.Id);
            Assert.Equal("{\"p\":1}", entry!.Payload);
            Assert.Equal("{\"s\":1}", entry.FlowStateJson);
            Assert.Equal("{\"w\":1}", entry.PersistedStateJson);

            Assert.True(await repository.TryRequestManualReplayAsync(deadLetter.Id, nodeId, DateTime.UtcNow, "{\"s\":2}"));
            Assert.False(await repository.TryRequestManualReplayAsync(deadLetter.Id, nodeId, DateTime.UtcNow, "{\"s\":3}"));
        }

        await using (var context = _db.NewContext())
        {
            Assert.Equal("{\"s\":2}", (await new ExecutionRepository(context).GetDeadLetterAsync(deadLetter.Id))!.FlowStateJson);

            // Deleting the run takes both cold rows with it.
            await context.FlowExecutions.Where(e => e.Id == run.Id).ExecuteDeleteAsync();
            Assert.Equal(0, await context.Set<FlowExecutionPayload>().CountAsync(p => p.FlowExecutionId == run.Id));
            Assert.Equal(0, await context.Set<DeadLetterPayload>().CountAsync(p => p.DeadLetterId == deadLetter.Id));
        }

        async Task<string?> XminAsync(Guid id)
        {
            await using var connection = await _db.DataSource!.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT xmin::text FROM \"FlowExecutionPayloads\" WHERE \"FlowExecutionId\" = @id;", connection);
            command.Parameters.AddWithValue("id", id);
            return (string?)await command.ExecuteScalarAsync();
        }
    }

    [PostgresFact]
    public async Task Start_up_moves_payload_columns_of_an_older_database_into_the_cold_tables()
    {
        var (flowId, nodeId) = await SeedFlowAsync("Legacy");
        var runId = Guid.NewGuid();
        var deadLetterId = Guid.NewGuid();
        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            await repository.AddFlowExecutionAsync(new FlowExecution { Id = runId, FlowId = flowId, FlowName = "Legacy" });
            await repository.AddDeadLetterEntryAsync(new DeadLetterEntry { Id = deadLetterId, FlowExecutionId = runId, StepId = nodeId });
        }

        // The layout before this change: payload columns on the hot rows (dead-letter ones NOT NULL), no cold rows.
        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        await using (var legacy = new NpgsqlCommand($$"""
            DELETE FROM "FlowExecutionPayloads" WHERE "FlowExecutionId" = '{{runId}}';
            DELETE FROM "DeadLetterPayloads" WHERE "DeadLetterId" = '{{deadLetterId}}';
            ALTER TABLE "FlowExecutions" ADD COLUMN "TriggerPayloadJson" text NULL, ADD COLUMN "TestDefinitionJson" text NULL,
                ADD COLUMN "FanOutItemJson" text NULL, ADD COLUMN "InitialFlowStateJson" text NULL, ADD COLUMN "InitialPersistedStateJson" text NULL;
            ALTER TABLE "DeadLetterEntries" ADD COLUMN "Payload" text NOT NULL DEFAULT '', ADD COLUMN "FlowStateJson" text NOT NULL DEFAULT '{}',
                ADD COLUMN "PersistedStateJson" text NOT NULL DEFAULT '{}';
            UPDATE "FlowExecutions" SET "TriggerPayloadJson" = '{"legacy":true}' WHERE "Id" = '{{runId}}';
            UPDATE "DeadLetterEntries" SET "Payload" = 'old payload', "FlowStateJson" = '{"old":1}' WHERE "Id" = '{{deadLetterId}}';
            """, connection))
        {
            await legacy.ExecuteNonQueryAsync();
        }

        await using (var context = _db.NewContext())
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context, NullLogger.Instance);
        }

        await using (var context = _db.NewContext())
        {
            var repository = new ExecutionRepository(context);
            Assert.Equal("{\"legacy\":true}", (await repository.GetFlowExecutionAsync(runId))!.TriggerPayloadJson);
            var entry = await repository.GetDeadLetterAsync(deadLetterId);
            Assert.Equal("old payload", entry!.Payload);
            Assert.Equal("{\"old\":1}", entry.FlowStateJson);

            // New rows insert without the dropped columns.
            await repository.AddDeadLetterEntryAsync(new DeadLetterEntry { FlowExecutionId = runId, StepId = nodeId, Payload = "new" });
        }

        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name IN ('FlowExecutions', 'DeadLetterEntries') " +
            "AND column_name IN ('TriggerPayloadJson', 'Payload', 'FlowStateJson', 'PersistedStateJson', 'InitialFlowStateJson');", connection))
        {
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
    }

    [PostgresFact]
    public async Task Start_up_adds_model_columns_an_older_database_is_missing()
    {
        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand(
            "ALTER TABLE \"FlowExecutions\" DROP COLUMN \"Message\"; ALTER TABLE \"FlowExecutions\" DROP COLUMN \"FanOutNodeName\";", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using (var context = _db.NewContext())
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context, NullLogger.Instance);
        }

        await using (var connection = await _db.DataSource!.OpenConnectionAsync())
        {
            var columns = await ColumnsAsync(connection, "FlowExecutions");
            Assert.Contains("Message", columns);
            Assert.Contains("FanOutNodeName", columns);
        }

        var (flowId, _) = await SeedFlowAsync("After upgrade");
        await using (var context = _db.NewContext())
        {
            await new ExecutionRepository(context).AddFlowExecutionAsync(new FlowExecution { FlowId = flowId, FlowName = "After upgrade", Message = "ok" });
        }
    }

    private async Task<(Guid FlowId, Guid NodeId)> SeedFlowAsync(string name)
    {
        var flowId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        await using var context = _db.NewContext();
        context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = name });
        context.IntegrationSteps.Add(new IntegrationStep { Id = nodeId, FlowId = flowId, NodeName = "Node", StepType = StepType.HttpAction });
        await context.SaveChangesAsync();
        return (flowId, nodeId);
    }

    private static async Task<HashSet<string>> ColumnsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table;", connection);
        command.Parameters.AddWithValue("table", table);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }
}
