using System;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

// The sink behind DatabaseLoggerProvider.
//
// The relational writer and the Elasticsearch writer are separate queues/pumps so each keeps its
// own batching and failure counters; the routing writer in front of them picks one per entry from
// the cached storage settings. Enqueue must never block and never throw — it runs on flow hot paths.
public interface IAppLogWriter : IDisposable
{
    bool TryEnqueue(AppLogEntry entry);

    void Flush();

    long DroppedCount { get; }

    long WrittenCount { get; }

    long WriteFailures { get; }

    int QueueDepth { get; }

    DateTime? LastSuccessfulWriteUtc { get; }

    // "Database" or "Elasticsearch" — reported by the log health endpoint.
    string Backend { get; }
}
