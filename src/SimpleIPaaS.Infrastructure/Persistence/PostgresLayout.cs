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
// headers and bodies. PostgreSQL keeps values over ~2 KB in out-of-line TOAST storage on top of that.
//
// The append-only tables are range-partitioned by day, so retention drops whole days (detach + drop) instead
// of deleting rows. FlowExecutions and StepExecutions are deliberately not partitioned: PostgreSQL requires the
// partition key in every unique key, and dead letters, steps and child runs reference a run by its id alone.
public static class PostgresLayout
{
    public const string StepExecutionPayloads = "StepExecutionPayloads";
    public const string StepPacketPayloads = "StepPacketPayloads";
    public const string StepPacketLogs = "StepPacketLogs";
    public const string AppLogEntries = "AppLogEntries";

    // Shadow properties on the hot rows (PostgreSQL model only).
    public const string ReceivedInputLength = "ReceivedInputLength";
    public const string RequestPayloadLength = "RequestPayloadLength";
    public const string ResponsePayloadLength = "ResponsePayloadLength";
    public const string RequestHeadersLength = "RequestHeadersLength";
    public const string RequestBodyLength = "RequestBodyLength";
    public const string ResponseHeadersLength = "ResponseHeadersLength";
    public const string ResponseBodyLength = "ResponseBodyLength";

    public sealed record PartitionedTable(string Name, string PartitionColumn, string[] PrimaryKey, string[] Indexes);

    // Every partitioned table, its partition key and what the conversion recreates on the partitioned parent.
    public static readonly PartitionedTable[] PartitionedTables =
    {
        new(StepPacketLogs, "StartedAt", new[] { "Id", "StartedAt" },
            new[] { "\"StepExecutionId\", \"Sequence\"", "\"StartedAt\"" }),
        new(StepPacketPayloads, "StartedAt", new[] { "PacketId", "StartedAt" }, Array.Empty<string>()),
        new(StepExecutionPayloads, "CreatedAt", new[] { "StepExecutionId", "CreatedAt" }, Array.Empty<string>()),
        new(AppLogEntries, "Timestamp", new[] { "Id", "Timestamp" },
            new[] { "\"Timestamp\"", "\"FlowExecutionId\"", "\"IntegrationId\"", "\"FlowId\"" }),
    };

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
    }
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
