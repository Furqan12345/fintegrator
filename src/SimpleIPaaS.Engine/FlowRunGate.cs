using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SimpleIPaaS.Engine;

// One run of a flow at a time, across every Engine.
//
// Cross-reference dedupe ("skip orders already seen") and Persisted State are read-check-write sequences, so two
// runs of the same flow overlapping would both treat the same order as new and both create it. Runs used to be
// serialised by an in-process lock only: with two Engines on PostgreSQL every order of a burst was created twice and
// counters lost updates.
//
// The gate is an in-process semaphore per flow plus, on PostgreSQL, a session-level advisory lock held on a
// dedicated connection for the whole run. If the Engine dies, its connection closes and PostgreSQL releases the
// lock at once. SQLite and SQL Server keep the in-process lock only: run a single Engine on those providers.
public sealed class FlowRunGate
{
    private const long KeySpace = 0x46_4C_4F_57_00_00_00_00; // "FLOW" in the high half
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _local = new();
    private readonly NpgsqlDataSource? _postgres;
    private readonly ILogger<FlowRunGate> _logger;

    public FlowRunGate(IServiceProvider services, ILogger<FlowRunGate> logger)
    {
        _postgres = services.GetService(typeof(NpgsqlDataSource)) as NpgsqlDataSource;
        _logger = logger;
    }

    public bool IsDistributed => _postgres != null;

    // Non-blocking: null when the flow is already running (here or on another Engine).
    public async Task<IAsyncDisposable?> TryEnterAsync(Guid flowId, CancellationToken cancellationToken = default)
    {
        var local = _local.GetOrAdd(flowId, _ => new SemaphoreSlim(1, 1));
        if (!await local.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (_postgres == null)
        {
            return new Lease(local, null, 0);
        }

        NpgsqlConnection? connection = null;
        try
        {
            connection = await _postgres.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var key = KeyFor(flowId);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                return new Lease(local, connection, key);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            local.Release();
            return null;
        }
        catch
        {
            if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
            local.Release();
            throw;
        }
    }

    // Waits until the flow is free (dead-letter replays queue behind a running run instead of overlapping it).
    public async Task<IAsyncDisposable> EnterAsync(Guid flowId, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (true)
        {
            var lease = await TryEnterAsync(flowId, cancellationToken).ConfigureAwait(false);
            if (lease != null)
            {
                return lease;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Flow {flowId} stayed busy for {maxWait.TotalSeconds:F0} s.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static long KeyFor(Guid flowId)
    {
        var bytes = flowId.ToByteArray();
        var low = BitConverter.ToUInt32(bytes, 0) ^ BitConverter.ToUInt32(bytes, 4) ^ BitConverter.ToUInt32(bytes, 8) ^ BitConverter.ToUInt32(bytes, 12);
        return KeySpace | low;
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _local;
        private readonly NpgsqlConnection? _connection;
        private readonly long _key;
        private int _disposed;

        public Lease(SemaphoreSlim local, NpgsqlConnection? connection, long key)
        {
            _local = local;
            _connection = connection;
            _key = key;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                if (_connection != null)
                {
                    try
                    {
                        await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
                        unlock.Parameters.AddWithValue("key", _key);
                        await unlock.ExecuteScalarAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // A broken connection: closing it below releases the session lock anyway.
                    }

                    await _connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _local.Release();
            }
        }
    }
}
