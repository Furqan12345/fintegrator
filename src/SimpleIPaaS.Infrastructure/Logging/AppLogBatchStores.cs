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

// PostgreSQL: one binary COPY per batch into the day-partitioned AppLogEntries. Retention is drop-only: whole days
// that fell out of the window, then the oldest whole days over the row cap (see Prune).
public sealed class PostgresAppLogBatchStore : IAppLogBatchStore, IDisposable
{
    private const string Columns =
        "\"EventId\", \"EventVersion\", \"Timestamp\", \"Level\", \"Source\", \"Service\", \"HostInstance\", \"EnvironmentName\", " +
        "\"ApplicationVersion\", \"Category\", \"Component\", \"EventName\", \"Operation\", \"Outcome\", \"DurationMs\", \"TraceId\", " +
        "\"SpanId\", \"ParentSpanId\", \"Message\", \"Exception\", \"FlowExecutionId\", \"FlowId\", \"FlowName\", \"IntegrationId\", " +
        "\"IntegrationName\", \"ConnectionId\", \"CardId\", \"CardType\", \"StepExecutionId\", \"Invocation\", \"LoopPath\", " +
        "\"RetryAttempt\", \"IsTest\", \"TestCaseId\", \"TestRunId\", \"NodeName\", \"PropertiesJson\"";

    private readonly Func<NpgsqlDataSource> _dataSource;
    private readonly Lazy<NpgsqlDataSource>? _owned;

    // Shares a data source (tests).
    public PostgresAppLogBatchStore(Func<NpgsqlDataSource> dataSource)
    {
        _dataSource = dataSource;
    }

    // Owns a small data source of its own. The container disposes its shared data source before it disposes the
    // log provider, so the final flush at shutdown used to fail with ObjectDisposedException and lose the last logs.
    public PostgresAppLogBatchStore(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "fusetor-logs", MaxPoolSize = 4 };
        _owned = new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(builder.ConnectionString));
        _dataSource = () => _owned.Value;
    }

    public void Dispose()
    {
        if (_owned is { IsValueCreated: true })
        {
            _owned.Value.Dispose();
        }
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

        // Drop-only: logs leave in whole days, never row by row. A row-level age delete would empty the oldest day
        // slice by slice (the cutoff moves every pass), so every row would be deleted singly and the day dropped
        // empty. A window therefore keeps between N and N+1 days. Both levels share one window (the longer; the
        // settings page sets them equal); 0 for either keeps everything by age.
        if (windows.DetailRetentionHours > 0 && windows.WarningRetentionHours > 0)
        {
            var longest = Math.Max(windows.DetailRetentionHours, windows.WarningRetentionHours);
            removed += (int)PostgresPartitions.DropOlderThanAsync(dataSource, PostgresLayout.AppLogEntries, DateTime.UtcNow.AddHours(-longest))
                .GetAwaiter().GetResult().Rows;
        }

        // The row cap also drops whole days, oldest first. Only when the newest day alone is over it (a runaway log
        // loop) does it delete rows, and then only in that day: it is a safety valve against a full disk.
        if (windows.MaxRows > 0)
        {
            var cap = PostgresPartitions.DropOldestOverCapAsync(dataSource, PostgresLayout.AppLogEntries, windows.MaxRows)
                .GetAwaiter().GetResult();
            removed += (int)cap.Rows;
            if (cap.KeptRows > windows.MaxRows)
            {
                using var connection = dataSource.OpenConnection();
                using var byCount = new NpgsqlCommand(
                    "DELETE FROM \"AppLogEntries\" WHERE \"Id\" <= COALESCE((SELECT max(\"Id\") FROM \"AppLogEntries\"), 0) - @maxRows;", connection);
                byCount.Parameters.AddWithValue("maxRows", (long)windows.MaxRows);
                removed += byCount.ExecuteNonQuery();
            }
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
