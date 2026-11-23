using System;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Infrastructure.Storage;

// The sink the logger provider talks to. Holds both writers (each with its own queue, batching and
// counters) and forwards each entry to whichever backend the logs dataset is configured for.
//
// Routing reads the storage runtime's cached snapshot: a volatile read, no services resolved and no
// database round-trip, because this runs on every log call — including the ones made while the
// container is still being built.
public sealed class RoutingLogWriter : IAppLogWriter
{
    private readonly IAppLogWriter _relational;
    private readonly IAppLogWriter _elasticsearch;
    private readonly StorageRuntime _runtime;

    public RoutingLogWriter(IAppLogWriter relational, IAppLogWriter elasticsearch, StorageRuntime runtime)
    {
        _relational = relational;
        _elasticsearch = elasticsearch;
        _runtime = runtime;
    }

    public string Backend => ElasticsearchActive ? _elasticsearch.Backend : _relational.Backend;

    private bool ElasticsearchActive
    {
        get
        {
            // Until the first refresh the relational writer takes the entries: losing logs to an
            // unknown configuration would be the worse failure.
            try
            {
                return _runtime.UsesElasticsearch(StorageDatasets.Logs);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public bool TryEnqueue(Domain.Entities.AppLogEntry entry) =>
        (ElasticsearchActive ? _elasticsearch : _relational).TryEnqueue(entry);

    public void Flush()
    {
        _relational.Flush();
        _elasticsearch.Flush();
    }

    public long DroppedCount => _relational.DroppedCount + _elasticsearch.DroppedCount;

    public long WrittenCount => _relational.WrittenCount + _elasticsearch.WrittenCount;

    public long WriteFailures => _relational.WriteFailures + _elasticsearch.WriteFailures;

    public int QueueDepth => _relational.QueueDepth + _elasticsearch.QueueDepth;

    public DateTime? LastSuccessfulWriteUtc =>
        _relational.LastSuccessfulWriteUtc is var relational && _elasticsearch.LastSuccessfulWriteUtc is var elastic
            ? relational > elastic ? relational : elastic
            : relational ?? elastic;

    public void Dispose()
    {
        _relational.Dispose();
        _elasticsearch.Dispose();
    }
}
