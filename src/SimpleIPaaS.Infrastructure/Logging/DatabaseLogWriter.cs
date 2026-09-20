using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Text.Json;
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
    private long _writeFailures;
    private long _lastSuccessfulWriteTicks;
    private long _lastFailureReportTicks;
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
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    public int QueueDepth => _channel.Reader.CanCount ? _channel.Reader.Count : 0;
    public DateTime? LastSuccessfulWriteUtc
        => Interlocked.Read(ref _lastSuccessfulWriteTicks) is var ticks && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

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
                Interlocked.Exchange(ref _lastSuccessfulWriteTicks, DateTime.UtcNow.Ticks);
                wroteAnything = true;
                TryPrune(connection);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _writeFailures);
                Interlocked.Add(ref _dropped, batch.Count);
                ReportSinkFailure(exception, batch.Count);
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
                (EventId, EventVersion, Timestamp, Level, Source, Service, HostInstance, EnvironmentName, ApplicationVersion, Category, Component, EventName, Operation, Outcome, DurationMs, TraceId, SpanId, ParentSpanId, Message, Exception, TenantId, FlowExecutionId, FlowId, FlowName, IntegrationId, IntegrationName, ConnectionId, CardId, CardType, StepExecutionId, Invocation, LoopPath, RetryAttempt, IsTest, TestCaseId, TestRunId, NodeName, PropertiesJson)
            VALUES
                ($eventId, $eventVersion, $timestamp, $level, $source, $service, $hostInstance, $environmentName, $applicationVersion, $category, $component, $eventName, $operation, $outcome, $durationMs, $traceId, $spanId, $parentSpanId, $message, $exception, $tenantId, $flowExecutionId, $flowId, $flowName, $integrationId, $integrationName, $connectionId, $cardId, $cardType, $stepExecutionId, $invocation, $loopPath, $retryAttempt, $isTest, $testCaseId, $testRunId, $nodeName, $propertiesJson);
            """;

        // DateTime/Guid parameters are serialised by Microsoft.Data.Sqlite using exactly the
        // same text formats EF Core uses, so the LogRepository reads them back correctly.
        var eventId = command.Parameters.Add("$eventId", SqliteType.Text);
        var eventVersion = command.Parameters.Add("$eventVersion", SqliteType.Integer);
        var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
        var level = command.Parameters.Add("$level", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var service = command.Parameters.Add("$service", SqliteType.Text);
        var hostInstance = command.Parameters.Add("$hostInstance", SqliteType.Text);
        var environmentName = command.Parameters.Add("$environmentName", SqliteType.Text);
        var applicationVersion = command.Parameters.Add("$applicationVersion", SqliteType.Text);
        var category = command.Parameters.Add("$category", SqliteType.Text);
        var component = command.Parameters.Add("$component", SqliteType.Text);
        var eventName = command.Parameters.Add("$eventName", SqliteType.Text);
        var operation = command.Parameters.Add("$operation", SqliteType.Text);
        var outcome = command.Parameters.Add("$outcome", SqliteType.Text);
        var durationMs = command.Parameters.Add("$durationMs", SqliteType.Integer);
        var traceId = command.Parameters.Add("$traceId", SqliteType.Text);
        var spanId = command.Parameters.Add("$spanId", SqliteType.Text);
        var parentSpanId = command.Parameters.Add("$parentSpanId", SqliteType.Text);
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var exception = command.Parameters.Add("$exception", SqliteType.Text);
        var tenantId = command.Parameters.Add("$tenantId", SqliteType.Text);
        var flowExecutionId = command.Parameters.Add("$flowExecutionId", SqliteType.Text);
        var flowId = command.Parameters.Add("$flowId", SqliteType.Text);
        var flowName = command.Parameters.Add("$flowName", SqliteType.Text);
        var integrationId = command.Parameters.Add("$integrationId", SqliteType.Text);
        var integrationName = command.Parameters.Add("$integrationName", SqliteType.Text);
        var connectionId = command.Parameters.Add("$connectionId", SqliteType.Text);
        var cardId = command.Parameters.Add("$cardId", SqliteType.Text);
        var cardType = command.Parameters.Add("$cardType", SqliteType.Text);
        var stepExecutionId = command.Parameters.Add("$stepExecutionId", SqliteType.Text);
        var invocation = command.Parameters.Add("$invocation", SqliteType.Integer);
        var loopPath = command.Parameters.Add("$loopPath", SqliteType.Text);
        var retryAttempt = command.Parameters.Add("$retryAttempt", SqliteType.Integer);
        var isTest = command.Parameters.Add("$isTest", SqliteType.Integer);
        var testCaseId = command.Parameters.Add("$testCaseId", SqliteType.Text);
        var testRunId = command.Parameters.Add("$testRunId", SqliteType.Text);
        var nodeName = command.Parameters.Add("$nodeName", SqliteType.Text);
        var propertiesJson = command.Parameters.Add("$propertiesJson", SqliteType.Text);

        foreach (var entry in batch)
        {
            eventId.Value = entry.EventId;
            eventVersion.Value = entry.EventVersion;
            timestamp.Value = entry.Timestamp;
            level.Value = entry.Level;
            source.Value = entry.Source;
            service.Value = entry.Service;
            hostInstance.Value = entry.HostInstance;
            environmentName.Value = entry.EnvironmentName;
            applicationVersion.Value = entry.ApplicationVersion;
            category.Value = entry.Category;
            component.Value = entry.Component;
            eventName.Value = entry.EventName;
            operation.Value = entry.Operation;
            outcome.Value = entry.Outcome;
            durationMs.Value = (object?)entry.DurationMs ?? DBNull.Value;
            traceId.Value = (object?)entry.TraceId ?? DBNull.Value;
            spanId.Value = (object?)entry.SpanId ?? DBNull.Value;
            parentSpanId.Value = (object?)entry.ParentSpanId ?? DBNull.Value;
            message.Value = entry.Message;
            exception.Value = (object?)entry.Exception ?? DBNull.Value;
            tenantId.Value = (object?)entry.TenantId ?? DBNull.Value;
            flowExecutionId.Value = (object?)entry.FlowExecutionId ?? DBNull.Value;
            flowId.Value = (object?)entry.FlowId ?? DBNull.Value;
            flowName.Value = (object?)entry.FlowName ?? DBNull.Value;
            integrationId.Value = (object?)entry.IntegrationId ?? DBNull.Value;
            integrationName.Value = (object?)entry.IntegrationName ?? DBNull.Value;
            connectionId.Value = (object?)entry.ConnectionId ?? DBNull.Value;
            cardId.Value = (object?)entry.CardId ?? DBNull.Value;
            cardType.Value = (object?)entry.CardType ?? DBNull.Value;
            stepExecutionId.Value = (object?)entry.StepExecutionId ?? DBNull.Value;
            invocation.Value = (object?)entry.Invocation ?? DBNull.Value;
            loopPath.Value = (object?)entry.LoopPath ?? DBNull.Value;
            retryAttempt.Value = (object?)entry.RetryAttempt ?? DBNull.Value;
            isTest.Value = (object?)entry.IsTest ?? DBNull.Value;
            testCaseId.Value = (object?)entry.TestCaseId ?? DBNull.Value;
            testRunId.Value = (object?)entry.TestRunId ?? DBNull.Value;
            nodeName.Value = (object?)entry.NodeName ?? DBNull.Value;
            propertiesJson.Value = entry.PropertiesJson;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void ReportSinkFailure(Exception exception, int batchCount)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastFailureReportTicks);
        if (lastTicks > 0 && nowTicks - lastTicks < TimeSpan.FromSeconds(30).Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastFailureReportTicks, nowTicks, lastTicks) != lastTicks)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine($"SimpleIPaaS observability sink failure; dropped batch={batchCount}, exception={exception.GetType().Name}");
        }
        catch
        {
            // The fallback sink must never affect execution or recursively log its own failure.
        }
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
            using var detailAge = connection.CreateCommand();
            detailAge.CommandText = "DELETE FROM AppLogEntries WHERE Timestamp < $cutoff AND Level NOT IN ('Warning', 'Error', 'Critical');";
            detailAge.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddHours(-options.RetentionHours));
            removed += detailAge.ExecuteNonQuery();
        }

        if (options.WarningRetentionHours > 0)
        {
            using var warningAge = connection.CreateCommand();
            warningAge.CommandText = "DELETE FROM AppLogEntries WHERE Timestamp < $cutoff AND Level IN ('Warning', 'Error', 'Critical');";
            warningAge.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddHours(-options.WarningRetentionHours));
            removed += warningAge.ExecuteNonQuery();
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
