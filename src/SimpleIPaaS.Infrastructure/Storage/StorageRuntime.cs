using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Elasticsearch;

namespace SimpleIPaaS.Infrastructure.Storage;

// The routing state every hot path reads: which backend serves each dataset, and the Elasticsearch
// client that follows from it.
//
// Why this type exists rather than injecting IStorageSettingsProvider wherever it is needed:
//
//   * The log sink is on every log call, including the calls made while the container is still
//     building itself (EncryptionService logs from its constructor, for instance). Resolving the
//     settings provider from there re-enters the container mid-construction and recurses forever.
//   * Reading a volatile snapshot is free, so the sink never waits on a database round-trip.
//
// One background loop (this type is also the hosted service both hosts register) refreshes the
// snapshot every few seconds, and a settings save invalidates it for an immediate refresh.
public sealed class StorageRuntime : IHostedService, IDisposable
{
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private StorageSettingsSnapshot _snapshot = StorageSettingsSnapshot.Default;
    private ElasticsearchClientProvider? _clients;
    private Task? _loop;
    private Task _pendingRefresh = Task.CompletedTask;
    private bool _disposed;

    public StorageRuntime(IServiceProvider services, TimeSpan? refreshInterval = null)
    {
        _services = services;
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
    }

    // Last known configuration. Never null, never throws, never blocks.
    public StorageSettingsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool UsesElasticsearch(string dataset) => Snapshot.UsesElasticsearch(dataset);

    // Non-blocking client lookup for writers.
    public bool TryGetClient(out ElasticsearchClient client, out StorageSettingsSnapshot snapshot)
    {
        snapshot = Snapshot;
        var clients = Volatile.Read(ref _clients);
        if (clients != null && clients.TryGet(out client, out snapshot))
        {
            return true;
        }

        client = null!;
        return false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop ??= Task.Run(() => LoopAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();

        if (_loop != null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown is best effort.
            }
        }
    }

    // Reads the settings through the container. Called from the background loop, never from a
    // logging call path.
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var settings = _services.GetService<IStorageSettingsProvider>();
            if (settings == null)
            {
                return;
            }

            var snapshot = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, snapshot);

            if (snapshot.ElasticsearchEnabled && snapshot.IsElasticsearchConfigured)
            {
                _clients ??= _services.GetService<ElasticsearchClientProvider>();
            }
        }
        catch (Exception)
        {
            // Keep the last known routing; the next tick tries again.
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // Called after a settings save so the change applies immediately rather than on the next tick.
    public void Invalidate()
    {
        Volatile.Write(ref _pendingRefresh, Task.Run(() => RefreshAsync(_shutdown.Token), CancellationToken.None));
    }

    // The refresh Invalidate started (completed when there is none). Lets a test — or a caller that
    // wants to observe the new routing — await the change instead of polling. Never touched by the
    // log path: TryEnqueue reads Snapshot only.
    public Task PendingRefresh => Volatile.Read(ref _pendingRefresh);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_refreshInterval);

        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        _refreshGate.Dispose();
    }
}
