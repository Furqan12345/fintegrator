using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Application.Interfaces;

// One place both hosts ask "where does this dataset live right now, and for how long is it kept?".
//
// The snapshot is cached briefly (a few seconds) and refreshed in the background, because callers
// include hot paths: a log write must never wait on a database round-trip to decide its sink.
public interface IStorageSettingsProvider
{
    Task<StorageSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    // Non-blocking read of the last loaded snapshot. False until the first successful load, so a
    // caller on a hot path can fall back to the relational store without blocking.
    bool TryGetCached(out StorageSettingsSnapshot snapshot);

    // Called after a save so the host that made the change switches immediately instead of after
    // the cache interval. Other hosts follow on their next refresh.
    void Invalidate();
}
