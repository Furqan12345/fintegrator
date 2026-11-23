using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;

namespace SimpleIPaaS.Infrastructure.Logging;

// Where DatabaseLogWriter puts a batch, and how it applies retention. Called only from the writer's single
// pump thread (and its final flush), never concurrently. Implementations must not log through ILogger.
public interface IAppLogBatchStore
{
    void Write(IReadOnlyList<AppLogEntry> batch);

    int Prune(LogRetentionWindows windows);
}

// The original SQLite sink: one transaction of prepared INSERTs per batch, DELETE-based retention.
public sealed class SqliteAppLogBatchStore : IAppLogBatchStore
{
    private readonly string _connectionString;
    private bool _schemaReady;

    public SqliteAppLogBatchStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Write(IReadOnlyList<AppLogEntry> batch)
    {
        using var connection = Open();
        DatabaseLogWriter.WriteSqliteBatch(connection, batch);
    }

    public int Prune(LogRetentionWindows windows)
    {
        using var connection = Open();
        return DatabaseLogWriter.Prune(connection, windows);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // Two processes share this file under WAL; wait on the other writer instead of failing with SQLITE_BUSY.
        SqliteTuning.Apply(connection);
        if (!_schemaReady)
        {
            DatabaseSchemaInitializer.EnsureAppLogEntriesTable(connection);
            _schemaReady = true;
        }

        return connection;
    }
}

// PostgreSQL: one binary COPY per batch into the day-partitioned AppLogEntries. Retention drops whole days that
// fell out of the longest window and deletes only what a shorter window still covers, in recent partitions.
public sealed class PostgresAppLogBatchStore : IAppLogBatchStore
{
    private const string Columns =
        "\"EventId\", \"EventVersion\", \"Timestamp\", \"Level\", \"Source\", \"Service\", \"HostInstance\", \"EnvironmentName\", " +
        "\"ApplicationVersion\", \"Category\", \"Component\", \"EventName\", \"Operation\", \"Outcome\", \"DurationMs\", \"TraceId\", " +
        "\"SpanId\", \"ParentSpanId\", \"Message\", \"Exception\", \"FlowExecutionId\", \"FlowId\", \"FlowName\", \"IntegrationId\", " +
        "\"IntegrationName\", \"ConnectionId\", \"CardId\", \"CardType\", \"StepExecutionId\", \"Invocation\", \"LoopPath\", " +
        "\"RetryAttempt\", \"IsTest\", \"TestCaseId\", \"TestRunId\", \"NodeName\", \"PropertiesJson\"";

    private readonly Func<NpgsqlDataSource> _dataSource;

    // The data source is resolved on first use: the log sink is built before the container can resolve it.
    public PostgresAppLogBatchStore(Func<NpgsqlDataSource> dataSource)
    {
        _dataSource = dataSource;
    }

    public void Write(IReadOnlyList<AppLogEntry> batch)
    {
        try
        {
            Copy(batch);
        }
        catch (Exception exception) when (PostgresPartitions.IsMissingPartition(exception))
        {
            var days = batch.Select(entry => PostgresPartitions.UtcDay(entry.Timestamp)).ToList();
            using (var connection = _dataSource().OpenConnection())
            {
                PostgresPartitions.EnsureRangeAsync(connection, days.Min(), days.Max()).GetAwaiter().GetResult();
            }

            Copy(batch);
        }
    }

    public int Prune(LogRetentionWindows windows)
    {
        var dataSource = _dataSource();
        var removed = 0;

        // Whole days older than the longest window go at once (0 = keep that class forever).
        if (windows.DetailRetentionHours > 0 && windows.WarningRetentionHours > 0)
        {
            var longest = Math.Max(windows.DetailRetentionHours, windows.WarningRetentionHours);
            removed += (int)PostgresPartitions.DropOlderThanAsync(dataSource, PostgresLayout.AppLogEntries, DateTime.UtcNow.AddHours(-longest))
                .GetAwaiter().GetResult().Rows;
        }

        using var connection = dataSource.OpenConnection();
        if (windows.DetailRetentionHours > 0)
        {
            removed += Execute(connection,
                "DELETE FROM \"AppLogEntries\" WHERE \"Timestamp\" < @cutoff AND \"Level\" NOT IN ('Warning', 'Error', 'Critical');",
                DateTime.UtcNow.AddHours(-windows.DetailRetentionHours));
        }

        if (windows.WarningRetentionHours > 0)
        {
            removed += Execute(connection,
                "DELETE FROM \"AppLogEntries\" WHERE \"Timestamp\" < @cutoff AND \"Level\" IN ('Warning', 'Error', 'Critical');",
                DateTime.UtcNow.AddHours(-windows.WarningRetentionHours));
        }

        if (windows.MaxRows > 0)
        {
            using var byCount = new NpgsqlCommand(
                "DELETE FROM \"AppLogEntries\" WHERE \"Id\" <= COALESCE((SELECT max(\"Id\") FROM \"AppLogEntries\"), 0) - @maxRows;", connection);
            byCount.Parameters.AddWithValue("maxRows", (long)windows.MaxRows);
            removed += byCount.ExecuteNonQuery();
        }

        return removed;
    }

    private void Copy(IReadOnlyList<AppLogEntry> batch)
    {
        using var connection = _dataSource().OpenConnection();
        using var importer = connection.BeginBinaryImport($"COPY \"AppLogEntries\" ({Columns}) FROM STDIN (FORMAT BINARY)");
        foreach (var entry in batch)
        {
            importer.StartRow();
            importer.Write(entry.EventId, NpgsqlDbType.Uuid);
            importer.Write(entry.EventVersion, NpgsqlDbType.Integer);
            importer.Write(Utc(entry.Timestamp), NpgsqlDbType.TimestampTz);
            Text(importer, entry.Level);
            Text(importer, entry.Source);
            Text(importer, entry.Service);
            Text(importer, entry.HostInstance);
            Text(importer, entry.EnvironmentName);
            Text(importer, entry.ApplicationVersion);
            Text(importer, entry.Category);
            Text(importer, entry.Component);
            Text(importer, entry.EventName);
            Text(importer, entry.Operation);
            Text(importer, entry.Outcome);
            Nullable(importer, entry.DurationMs, NpgsqlDbType.Bigint);
            NullableText(importer, entry.TraceId);
            NullableText(importer, entry.SpanId);
            NullableText(importer, entry.ParentSpanId);
            Text(importer, entry.Message);
            NullableText(importer, entry.Exception);
            Nullable(importer, entry.FlowExecutionId, NpgsqlDbType.Uuid);
            Nullable(importer, entry.FlowId, NpgsqlDbType.Uuid);
            NullableText(importer, entry.FlowName);
            Nullable(importer, entry.IntegrationId, NpgsqlDbType.Uuid);
            NullableText(importer, entry.IntegrationName);
            Nullable(importer, entry.ConnectionId, NpgsqlDbType.Uuid);
            Nullable(importer, entry.CardId, NpgsqlDbType.Uuid);
            NullableText(importer, entry.CardType);
            Nullable(importer, entry.StepExecutionId, NpgsqlDbType.Uuid);
            Nullable(importer, entry.Invocation, NpgsqlDbType.Integer);
            NullableText(importer, entry.LoopPath);
            Nullable(importer, entry.RetryAttempt, NpgsqlDbType.Integer);
            Nullable(importer, entry.IsTest, NpgsqlDbType.Boolean);
            Nullable(importer, entry.TestCaseId, NpgsqlDbType.Uuid);
            Nullable(importer, entry.TestRunId, NpgsqlDbType.Uuid);
            NullableText(importer, entry.NodeName);
            Text(importer, entry.PropertiesJson);
        }

        importer.Complete();
    }

    private static int Execute(NpgsqlConnection connection, string sql, DateTime cutoff)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
        return command.ExecuteNonQuery();
    }

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static void Text(NpgsqlBinaryImporter importer, string? value) =>
        importer.Write(Storage.PostgresPacketIngestor.Clean(value), NpgsqlDbType.Text);

    private static void NullableText(NpgsqlBinaryImporter importer, string? value)
    {
        if (value == null) importer.WriteNull();
        else importer.Write(Storage.PostgresPacketIngestor.Clean(value), NpgsqlDbType.Text);
    }

    private static void Nullable<T>(NpgsqlBinaryImporter importer, T? value, NpgsqlDbType type) where T : struct
    {
        if (value.HasValue) importer.Write(value.Value, type);
        else importer.WriteNull();
    }
}
