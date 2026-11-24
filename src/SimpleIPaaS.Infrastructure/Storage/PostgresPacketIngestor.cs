using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Asynchronous, batched packet-log ingestion for PostgreSQL.
//
// A flow step hands its HTTP packet to a bounded channel and carries on; one consumer per process drains it in
// batches of up to BatchSize and writes each batch with two binary COPYs (hot StepPacketLogs, cold
// StepPacketPayloads) in one transaction. A burst of 500 calls costs one round trip instead of 500.
//
// Packets are audit data, so a full channel applies backpressure (the step waits) instead of dropping. A batch
// that fails is retried; a missing day partition is created and the batch retried. Stop drains the channel.
// Packets become readable within FlushInterval of being produced.
public sealed class PostgresPacketIngestor : BackgroundService
{
    public const int BatchSize = 500;
    public const int Capacity = 20_000;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);
    private const int MaxAttempts = 3;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresPacketIngestor> _logger;
    private readonly Channel<StepPacketLog> _channel = Channel.CreateBounded<StepPacketLog>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // Set when the host starts stopping: from then on a database outage may delay shutdown only briefly.
    private DateTime? _stoppingSince;
    private long _written;
    private long _batches;
    private long _dropped;

    public PostgresPacketIngestor(NpgsqlDataSource dataSource, ILogger<PostgresPacketIngestor> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public long WrittenCount => Interlocked.Read(ref _written);
    public long BatchCount => Interlocked.Read(ref _batches);
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public int QueueDepth => _channel.Reader.Count;

    public ValueTask EnqueueAsync(StepPacketLog packet, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(packet, cancellationToken);

    // Writes everything queued so far (tests, and callers that must read their own packets back immediately).
    public Task FlushAsync(CancellationToken cancellationToken = default) => DrainAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // Coalesce a burst: wait out the flush interval unless a full batch is already waiting.
                if (_channel.Reader.Count < BatchSize)
                {
                    await Task.Delay(FlushInterval, stoppingToken).ConfigureAwait(false);
                }

                await DrainAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: StopAsync drains what is left.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingSince = DateTime.UtcNow;
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = new List<StepPacketLog>(BatchSize);
            while (true)
            {
                batch.Clear();
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var packet))
                {
                    batch.Add(packet);
                }

                if (batch.Count == 0)
                {
                    return;
                }

                await WriteWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteWithRetryAsync(List<StepPacketLog> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteBatchAsync(_dataSource, batch, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _written, batch.Count);
                Interlocked.Increment(ref _batches);
                return;
            }
            catch (Exception exception) when (DatabaseStartup.IsConnectionFailure(exception) && !GiveUpOnOutage())
            {
                // The database is unreachable (restart, failover, network). Keep the batch and wait: the bounded
                // channel turns a long outage into backpressure on the steps instead of lost packets.
                if (attempt == 1 || attempt % 20 == 0)
                {
                    _logger.LogWarning("Packet log batch of {Count} is waiting for the database ({Reason}); attempt {Attempt}.",
                        batch.Count, exception.GetBaseException().Message, attempt);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500 * attempt, 5000)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaxAttempts)
            {
                if (PostgresPartitions.IsMissingPartition(exception))
                {
                    await EnsurePartitionsForAsync(batch, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogWarning(exception, "Packet log batch of {Count} failed (attempt {Attempt}); retrying.", batch.Count, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.Add(ref _dropped, batch.Count);
                _logger.LogError(exception, "Packet log batch of {Count} could not be written after {Attempts} attempts and was dropped.", batch.Count, MaxAttempts);
                return;
            }
        }
    }

    private bool GiveUpOnOutage() => _stoppingSince is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(10);

    private async Task EnsurePartitionsForAsync(List<StepPacketLog> batch, CancellationToken cancellationToken)
    {
        var days = batch.Select(packet => PostgresPartitions.UtcDay(packet.StartedAt)).ToList();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // No lock timeout: this batch's day is missing, so it waits for the lock rather than lose its rows.
        await PostgresPartitions.EnsureRangeAsync(connection, days.Min(), days.Max(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteBatchAsync(NpgsqlDataSource dataSource, IReadOnlyList<StepPacketLog> batch, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var hot = await connection.BeginBinaryImportAsync(
            "COPY \"StepPacketLogs\" (\"Id\", \"StepExecutionId\", \"Sequence\", \"Kind\", \"PageNumber\", \"Attempt\", \"HttpMethod\", \"RequestUrl\", " +
            "\"StatusCode\", \"StartedAt\", \"CompletedAt\", \"DurationMs\", \"Error\", " +
            "\"RequestHeadersLength\", \"RequestBodyLength\", \"ResponseHeadersLength\", \"ResponseBodyLength\") FROM STDIN (FORMAT BINARY)",
            cancellationToken).ConfigureAwait(false))
        {
            foreach (var packet in batch)
            {
                await hot.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(packet.Id, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(packet.StepExecutionId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(packet.Sequence, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(Clean(packet.Kind), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(hot, packet.PageNumber, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(hot, packet.Attempt, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(Clean(packet.HttpMethod), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(Clean(packet.RequestUrl), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(hot, packet.StatusCode, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(Utc(packet.StartedAt), NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                if (packet.CompletedAt.HasValue)
                    await hot.WriteAsync(Utc(packet.CompletedAt.Value), NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                else
                    await hot.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                await WriteNullableAsync(hot, packet.DurationMs, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
                if (packet.Error != null)
                    await hot.WriteAsync(Clean(packet.Error), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                else
                    await hot.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(packet.RequestHeadersJson?.Length ?? 0, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(packet.RequestBody?.Length ?? 0, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(packet.ResponseHeadersJson?.Length ?? 0, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await hot.WriteAsync(packet.ResponseBody?.Length ?? 0, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            }

            await hot.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cold = await connection.BeginBinaryImportAsync(
            "COPY \"StepPacketPayloads\" (\"PacketId\", \"StartedAt\", \"RequestHeadersJson\", \"RequestBody\", \"ResponseHeadersJson\", \"ResponseBody\") FROM STDIN (FORMAT BINARY)",
            cancellationToken).ConfigureAwait(false))
        {
            foreach (var packet in batch)
            {
                await cold.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await cold.WriteAsync(packet.Id, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await cold.WriteAsync(Utc(packet.StartedAt), NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                await cold.WriteAsync(Clean(packet.RequestHeadersJson), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await cold.WriteAsync(Clean(packet.RequestBody), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await cold.WriteAsync(Clean(packet.ResponseHeadersJson), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await cold.WriteAsync(Clean(packet.ResponseBody), NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            }

            await cold.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter importer, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
        where T : struct
    {
        if (value.HasValue)
            await importer.WriteAsync(value.Value, type, cancellationToken).ConfigureAwait(false);
        else
            await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
    }

    // Same truncation as the hot row's StartedAt, so the cold key matches it exactly.
    internal static DateTime Utc(DateTime value) => ExecutionRepository.TruncateToMicroseconds(value);

    // PostgreSQL text cannot hold NUL; a binary response decoded as text can. One bad packet must not sink a batch.
    internal static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Contains('\0') ? value.Replace('\0', '\uFFFD') : value;
}
