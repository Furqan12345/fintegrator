using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Persistence;

// The PostgreSQL storage layout (SQLite and SQL Server keep the single-table layout).
//
// Hot tables hold the metadata every list, filter and dashboard reads: FlowExecutions, StepExecutions and
// StepPacketLogs. They are narrow because the heavy text lives in the cold tables below, and they carry the
// payload sizes so a summary never has to touch a payload.
//
// Cold tables hold what only the data inspector opens: a step's input/request/response and an HTTP call's
// headers and bodies, a run's trigger payload, test definition and start state, and a dead letter's payload and
// flow state. PostgreSQL keeps values over ~2 KB in out-of-line TOAST storage on top of that.
//
// The append-only tables are range-partitioned by day, so retention drops whole days (detach + drop) instead
// of deleting rows. AppLogEntries is split by level first (warnings and errors, everything else), each part by day,
// so the two parts can have different windows; reads still go through AppLogEntries. FlowExecutions and StepExecutions are deliberately not partitioned: PostgreSQL requires the
// partition key in every unique key, and dead letters, steps and child runs reference a run by its id alone.
public static class PostgresLayout
{
    public const string StepExecutionPayloads = "StepExecutionPayloads";
    public const string StepPacketPayloads = "StepPacketPayloads";
    public const string StepPacketLogs = "StepPacketLogs";
    public const string AppLogEntries = "AppLogEntries";
    public const string AppLogWarnings = "AppLogEntries_warnings";
    public const string AppLogDetail = "AppLogEntries_detail";
    public static readonly string[] WarningLevels = { "Warning", "Error", "Critical" };
    public const string FlowExecutions = "FlowExecutions";
    public const string DeadLetterEntries = "DeadLetterEntries";
    public const string FlowExecutionPayloads = "FlowExecutionPayloads";
    public const string DeadLetterPayloads = "DeadLetterPayloads";

    // Shadow properties on the hot rows (PostgreSQL model only).
    public const string ReceivedInputLength = "ReceivedInputLength";
    public const string RequestPayloadLength = "RequestPayloadLength";
    public const string ResponsePayloadLength = "ResponsePayloadLength";
    public const string RequestHeadersLength = "RequestHeadersLength";
    public const string RequestBodyLength = "RequestBodyLength";
    public const string ResponseHeadersLength = "ResponseHeadersLength";
    public const string ResponseBodyLength = "ResponseBodyLength";

    public sealed record PartitionedTable(string Name, string PartitionColumn, string[] PrimaryKey, string[] Indexes);

    // The tables partitioned by day directly, their partition key and what the conversion recreates on the parent.
    public static readonly PartitionedTable[] PartitionedTables =
    {
        new(StepPacketLogs, "StartedAt", new[] { "Id", "StartedAt" },
            new[] { "\"StepExecutionId\", \"Sequence\"", "\"StartedAt\"" }),
        new(StepPacketPayloads, "StartedAt", new[] { "PacketId", "StartedAt" }, Array.Empty<string>()),
        new(StepExecutionPayloads, "CreatedAt", new[] { "StepExecutionId", "CreatedAt" }, Array.Empty<string>()),
    };

    // AppLogEntries: list-partitioned on Level into AppLogWarnings (WarningLevels) and AppLogDetail (the default
    // partition, every other level), each range-partitioned by day on Timestamp.
    public static readonly PartitionedTable AppLogs = new(AppLogEntries, "Timestamp", new[] { "Id", "Timestamp", "Level" },
        new[] { "\"Timestamp\"", "\"FlowExecutionId\"", "\"IntegrationId\"", "\"FlowId\"" });

    // Every table whose children are day partitions (what EnsureRange and the drops work on).
    public static readonly string[] DayPartitionedTables =
        PartitionedTables.Select(table => table.Name).Concat(new[] { AppLogWarnings, AppLogDetail }).ToArray();

    public static bool IsPostgres(this DbContext context) => context.Database.IsNpgsql();

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StepExecution>(entity =>
        {
            entity.Ignore(step => step.ReceivedInput);
            entity.Ignore(step => step.RequestPayload);
            entity.Ignore(step => step.ResponsePayload);
            entity.Property<int>(ReceivedInputLength);
            entity.Property<int>(RequestPayloadLength);
            entity.Property<int>(ResponsePayloadLength);
        });

        modelBuilder.Entity<StepPacketLog>(entity =>
        {
            entity.Ignore(packet => packet.RequestHeadersJson);
            entity.Ignore(packet => packet.RequestBody);
            entity.Ignore(packet => packet.ResponseHeadersJson);
            entity.Ignore(packet => packet.ResponseBody);
            entity.Property<int>(RequestHeadersLength);
            entity.Property<int>(RequestBodyLength);
            entity.Property<int>(ResponseHeadersLength);
            entity.Property<int>(ResponseBodyLength);
        });

        modelBuilder.Entity<StepExecutionPayload>(entity =>
        {
            entity.ToTable(StepExecutionPayloads);
            entity.HasKey(payload => new { payload.StepExecutionId, payload.CreatedAt });
        });

        modelBuilder.Entity<StepPacketPayload>(entity =>
        {
            entity.ToTable(StepPacketPayloads);
            entity.HasKey(payload => new { payload.PacketId, payload.StartedAt });
        });

        // A run's and a dead letter's large JSON is written once, when the row is created; status updates rewrite
        // the hot row many times per run and must not rewrite (and re-TOAST) a megabyte webhook body each time.
        modelBuilder.Entity<FlowExecution>(entity =>
        {
            entity.Ignore(run => run.TriggerPayloadJson);
            entity.Ignore(run => run.TestDefinitionJson);
            entity.Ignore(run => run.FanOutItemJson);
            entity.Ignore(run => run.InitialFlowStateJson);
            entity.Ignore(run => run.InitialPersistedStateJson);
        });

        modelBuilder.Entity<DeadLetterEntry>(entity =>
        {
            entity.Ignore(entry => entry.Payload);
            entity.Ignore(entry => entry.FlowStateJson);
            entity.Ignore(entry => entry.PersistedStateJson);
        });

        modelBuilder.Entity<FlowExecutionPayload>(entity =>
        {
            entity.ToTable(FlowExecutionPayloads);
            entity.HasKey(payload => payload.FlowExecutionId);
            entity.HasOne<FlowExecution>().WithOne().HasForeignKey<FlowExecutionPayload>(payload => payload.FlowExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeadLetterPayload>(entity =>
        {
            entity.ToTable(DeadLetterPayloads);
            entity.HasKey(payload => payload.DeadLetterId);
            entity.HasOne<DeadLetterEntry>().WithOne().HasForeignKey<DeadLetterPayload>(payload => payload.DeadLetterId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    // Columns that lived on the hot rows before the cold tables existed (moved once by PostgresSchema).
    internal static readonly (string Table, string ColdTable, string KeyColumn, string[] Columns)[] MovedColumns =
    {
        (FlowExecutions, FlowExecutionPayloads, "FlowExecutionId",
            new[] { "TriggerPayloadJson", "TestDefinitionJson", "FanOutItemJson", "InitialFlowStateJson", "InitialPersistedStateJson" }),
        (DeadLetterEntries, DeadLetterPayloads, "DeadLetterId", new[] { "Payload", "FlowStateJson", "PersistedStateJson" }),
    };
}

// Cold half of a FlowExecution (PostgreSQL only): written when the run is created, deleted with it.
public sealed class FlowExecutionPayload
{
    public Guid FlowExecutionId { get; set; }
    public string? TriggerPayloadJson { get; set; }
    public string? TestDefinitionJson { get; set; }
    public string? FanOutItemJson { get; set; }
    public string? InitialFlowStateJson { get; set; }
    public string? InitialPersistedStateJson { get; set; }
}

// Cold half of a DeadLetterEntry (PostgreSQL only).
public sealed class DeadLetterPayload
{
    public Guid DeadLetterId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string FlowStateJson { get; set; } = "{}";
    public string PersistedStateJson { get; set; } = "{}";
}

// Cold half of a StepExecution (PostgreSQL only). CreatedAt is the step's StartedAt, which never changes after
// the step is inserted, so an update addresses exactly one partition.
public sealed class StepExecutionPayload
{
    public Guid StepExecutionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ReceivedInput { get; set; } = string.Empty;
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
}

// Cold half of a StepPacketLog (PostgreSQL only).
public sealed class StepPacketPayload
{
    public Guid PacketId { get; set; }
    public DateTime StartedAt { get; set; }
    public string RequestHeadersJson { get; set; } = "{}";
    public string RequestBody { get; set; } = string.Empty;
    public string ResponseHeadersJson { get; set; } = "{}";
    public string ResponseBody { get; set; } = string.Empty;
}
