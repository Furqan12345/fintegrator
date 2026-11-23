using System;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Logging;

namespace SimpleIPaaS.Infrastructure.Storage;

// Adapts the relational batched writer to the sink contract the logger provider uses, so the
// provider can hold either backend (or the router in front of both) without knowing about it.
public sealed class SqlAppLogWriter : IAppLogWriter
{
    private readonly DatabaseLogWriter _writer;

    public SqlAppLogWriter(DatabaseLogWriter writer)
    {
        _writer = writer;
    }

    public string Backend => "Database";

    public long DroppedCount => _writer.DroppedCount;

    public long WrittenCount => _writer.WrittenCount;

    public long WriteFailures => _writer.WriteFailures;

    public int QueueDepth => _writer.QueueDepth;

    public DateTime? LastSuccessfulWriteUtc => _writer.LastSuccessfulWriteUtc;

    public bool TryEnqueue(AppLogEntry entry) => _writer.TryEnqueue(entry);

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();
}
