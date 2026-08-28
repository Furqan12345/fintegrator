using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Logging;

// The batched, non-blocking sink behind DatabaseLoggerProvider.
//
// Design constraints that drive every decision here:
//  * Enqueueing must never block and never throw — it happens on flow-execution hot paths.
//  * Writes must never go through EF Core. EF logs its own SQL through ILogger, and any
//    such entry reaching this writer would generate another INSERT, which would generate
//    another log entry... The provider filters EF categories out, and this raw ADO.NET
//    write path means EF is never even involved on the write side. Belt and braces.
//  * The table is append-only and pruned, so it cannot grow without bound.
public sealed class DatabaseLogWriter : IDisposable
{
    private readonly DatabaseLoggerOptions _options;
    private readonly string _connectionString;
    private readonly Channel<AppLogEntry> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private bool _schemaReady;
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private long _dropped;
    private long _written;
    private bool _disposed;

    public DatabaseLogWriter(DatabaseLoggerOptions options, string connectionString)
    {
        _options = options;
        _connectionString = connectionString;

        var capacity = _options.QueueCapacity < 16 ? 16 : _options.QueueCapacity;
        _channel = Channel.CreateBounded<AppLogEntry>(new BoundedChannelOptions(capacity)
        {
            // Paired with TryWrite (never WriteAsync), FullMode.Wait means a full backlog
            // makes TryWrite return false immediately — it never blocks the caller, and the
            // rejected entry is counted as dropped rather than silently discarded the way
            // DropWrite would. A stalled or locked database must never wedge the Engine.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _pump = Task.Run(() => PumpAsync(_shutdown.Token));
    }

    // Entries dropped because the in-memory backlog was full (diagnostics/tests).
    public long DroppedCount => Interlocked.Read(ref _dropped);

    // Entries successfully persisted (diagnostics/tests).
    public long WrittenCount => Interlocked.Read(ref _written);

    public bool TryEnqueue(AppLogEntry entry)
    {
        if (_disposed)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(entry))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    // Test/shutdown hook: drain everything currently queued synchronously.
    public void Flush()
    {
        DrainAndWrite();
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Wake either when an entry arrives or when retention is due, so an idle host
            // still prunes rows written by the other host.
            using (var wake = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                wake.CancelAfter(_options.PruneInterval);
                try
                {
                    if (!await _channel.Reader.WaitToReadAsync(wake.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    TryPrune();
                    continue;
                }
            }

            // Coalesce a burst into a single transaction: wait out the flush interval unless
            // the backlog already reached the batch size, in which case write immediately.
            if (!IsBatchFull())
            {
                try
                {
                    await Task.Delay(_options.FlushInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Fall through: still write what we have before exiting.
                }
            }

            DrainAndWrite();
        }

        DrainAndWrite();
    }

    private bool IsBatchFull()
    {
        var batchSize = _options.BatchSize < 1 ? 1 : _options.BatchSize;
        return _channel.Reader.CanCount && _channel.Reader.Count >= batchSize;
    }

    private void DrainAndWrite()
    {
        var batchSize = _options.BatchSize < 1 ? 1 : _options.BatchSize;
        var batch = new List<AppLogEntry>(batchSize);
        var wroteAnything = false;

        while (true)
        {
            batch.Clear();
            while (batch.Count < batchSize && _channel.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                break;
            }

            // Never let a sink failure escape into the process. Worst case we lose log rows.
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                ApplyConnectionPragmas(connection);
                EnsureSchema(connection);
                WriteBatch(connection, batch);
                Interlocked.Add(ref _written, batch.Count);
                wroteAnything = true;
                TryPrune(connection);
            }
            catch (Exception)
            {
                Interlocked.Add(ref _dropped, batch.Count);
            }
        }

        if (!wroteAnything)
        {
            // Still give retention a chance to run on an idle host.
            TryPrune();
        }
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        // Two processes share this file under WAL; wait on the other writer instead of
        // failing the batch with SQLITE_BUSY.
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        if (_schemaReady)
        {
            return;
        }

        DatabaseSchemaInitializer.EnsureAppLogEntriesTable(connection);
        _schemaReady = true;
    }

    private static void WriteBatch(SqliteConnection connection, List<AppLogEntry> batch)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AppLogEntries
                (Timestamp, Level, Source, Category, Message, Exception, TenantId, FlowExecutionId, FlowId, NodeName)
            VALUES
                ($timestamp, $level, $source, $category, $message, $exception, $tenantId, $flowExecutionId, $flowId, $nodeName);
            """;

        // DateTime/Guid parameters are serialised by Microsoft.Data.Sqlite using exactly the
        // same text formats EF Core uses, so the LogRepository reads them back correctly.
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
        var level = command.Parameters.Add("$level", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var category = command.Parameters.Add("$category", SqliteType.Text);
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var exception = command.Parameters.Add("$exception", SqliteType.Text);
        var tenantId = command.Parameters.Add("$tenantId", SqliteType.Text);
        var flowExecutionId = command.Parameters.Add("$flowExecutionId", SqliteType.Text);
        var flowId = command.Parameters.Add("$flowId", SqliteType.Text);
        var nodeName = command.Parameters.Add("$nodeName", SqliteType.Text);

        foreach (var entry in batch)
        {
            timestamp.Value = entry.Timestamp;
            level.Value = entry.Level;
            source.Value = entry.Source;
            category.Value = entry.Category;
            message.Value = entry.Message;
            exception.Value = (object?)entry.Exception ?? DBNull.Value;
            tenantId.Value = (object?)entry.TenantId ?? DBNull.Value;
            flowExecutionId.Value = (object?)entry.FlowExecutionId ?? DBNull.Value;
            flowId.Value = (object?)entry.FlowId ?? DBNull.Value;
            nodeName.Value = (object?)entry.NodeName ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void TryPrune()
    {
        if (!IsPruneDue())
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            ApplyConnectionPragmas(connection);
            EnsureSchema(connection);
            TryPrune(connection);
        }
        catch (Exception)
        {
            // Retention is best effort; it will be retried on the next interval.
        }
    }

    private bool IsPruneDue() => DateTime.UtcNow - _lastPruneUtc >= _options.PruneInterval;

    private void TryPrune(SqliteConnection connection)
    {
        if (!IsPruneDue())
        {
            return;
        }

        _lastPruneUtc = DateTime.UtcNow;
        Prune(connection, _options);
    }

    // Retention, exposed for tests. Age-based pruning first, then the hard row cap.
    public static int Prune(SqliteConnection connection, DatabaseLoggerOptions options)
    {
        var removed = 0;

        if (options.RetentionHours > 0)
        {
            using var byAge = connection.CreateCommand();
            byAge.CommandText = "DELETE FROM AppLogEntries WHERE Timestamp < $cutoff;";
            byAge.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddHours(-options.RetentionHours));
            removed += byAge.ExecuteNonQuery();
        }

        if (options.MaxRows > 0)
        {
            using var byCount = connection.CreateCommand();
            byCount.CommandText = """
                DELETE FROM AppLogEntries
                WHERE Id <= COALESCE((SELECT MAX(Id) FROM AppLogEntries), 0) - $maxRows;
                """;
            byCount.Parameters.AddWithValue("$maxRows", options.MaxRows);
            removed += byCount.ExecuteNonQuery();
        }

        return removed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();

        try
        {
            _shutdown.Cancel();
            _pump.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutdown is best effort.
        }

        try
        {
            DrainAndWrite();
        }
        catch (Exception)
        {
            // Ditto.
        }

        _shutdown.Dispose();
    }
}
