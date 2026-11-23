using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Reads the single StorageSettings row, decrypts its secrets and caches the result for a few
// seconds.
//
// The cache is what makes routing cheap: the log sink asks "where do logs live?" on every entry
// (potentially thousands per second) and must not touch the database to answer. A short TTL also
// means the Engine — a separate process — picks up a change made on the settings page without a
// restart. It runs in its own scope because both the Api and the Engine resolve this as a singleton
// while IPaaSContext is scoped.
//
// Deliberately takes no ILogger: the container resolves ILogger<T> through ILoggerFactory, which
// resolves every ILoggerProvider, and the log sink needs this provider to decide its backend. That
// is a dependency cycle the container's validation walks forever, so failures are reported on the
// error stream instead — the same fallback the log writers themselves use.
public sealed class StorageSettingsProvider : IStorageSettingsProvider, IDisposable
{
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEncryptionService _encryption;
    private readonly TimeSpan _cacheDuration;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Timer _refreshTimer;

    private StorageSettingsSnapshot? _snapshot;
    private DateTime _loadedAtUtc = DateTime.MinValue;
    private long _lastFailureReportTicks;
    private bool _disposed;

    public StorageSettingsProvider(
        IServiceScopeFactory scopeFactory,
        IEncryptionService encryption,
        TimeSpan? cacheDuration = null)
    {
        _scopeFactory = scopeFactory;
        _encryption = encryption;
        _cacheDuration = cacheDuration ?? DefaultCacheDuration;

        // Keeps an idle host — the Engine between polls, or the Api overnight — from sitting on a
        // stale configuration, without making every caller refresh it.
        _refreshTimer = new Timer(
            _ => _ = RefreshAsync(CancellationToken.None),
            null,
            _cacheDuration,
            _cacheDuration);
    }

    public async Task<StorageSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var current = Volatile.Read(ref _snapshot);
        if (current != null && DateTime.UtcNow - _loadedAtUtc < _cacheDuration)
        {
            return current;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetCached(out StorageSettingsSnapshot snapshot)
    {
        var current = Volatile.Read(ref _snapshot);
        snapshot = current ?? StorageSettingsSnapshot.Default;
        return current != null;
    }

    public void Invalidate()
    {
        _loadedAtUtc = DateTime.MinValue;
        _ = RefreshAsync(CancellationToken.None);
    }

    private async Task<StorageSettingsSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return Volatile.Read(ref _snapshot) ?? StorageSettingsSnapshot.Default;
        }

        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Volatile.Read(ref _snapshot) ?? StorageSettingsSnapshot.Default;
        }

        try
        {
            var current = Volatile.Read(ref _snapshot);
            if (current != null && DateTime.UtcNow - _loadedAtUtc < _cacheDuration)
            {
                return current;
            }

            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, loaded);
            _loadedAtUtc = DateTime.UtcNow;
            return loaded;
        }
        catch (Exception exception)
        {
            // A database hiccup must not take the host down, and must not flip a dataset to the
            // wrong backend: keep the last known configuration and try again on the next tick.
            ReportFailure("could not read the storage settings; keeping the last known configuration", exception);
            return Volatile.Read(ref _snapshot) ?? StorageSettingsSnapshot.Default;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<StorageSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IPaaSContext>();

        var row = await context.StorageSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var snapshot = StorageCatalog.Snapshot(row);

        return snapshot with
        {
            Password = await DecryptAsync(row?.EncryptedElasticsearchPassword, cancellationToken).ConfigureAwait(false),
            ApiKey = await DecryptAsync(row?.EncryptedElasticsearchApiKey, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<string> DecryptAsync(string? encrypted, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return string.Empty;
        }

        try
        {
            return await _encryption.DecryptAsync(encrypted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A rotated or missing encryption key must not break log writes; the cluster call will
            // fail with an authentication error the settings page shows instead.
            ReportFailure("could not decrypt a stored Elasticsearch credential; treating it as unset", exception);
            return string.Empty;
        }
    }

    // Best effort, throttled, and never recursive: this runs on the settings timer.
    private void ReportFailure(string message, Exception exception)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastFailureReportTicks);
        if (lastTicks > 0 && nowTicks - lastTicks < TimeSpan.FromMinutes(1).Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastFailureReportTicks, nowTicks, lastTicks) != lastTicks)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine($"fusetor storage settings: {message} ({exception.GetType().Name}: {exception.Message})");
        }
        catch
        {
            // Reporting must never affect execution.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Dispose();
        _refreshGate.Dispose();
    }
}
