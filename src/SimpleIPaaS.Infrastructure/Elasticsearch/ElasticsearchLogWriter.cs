using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Logging;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Batched log sink for the Elasticsearch logs dataset. Same contract as DatabaseLogWriter:
// enqueueing never blocks and never throws, and a failure is counted rather than propagated.
//
// Documents go to the dataset's write alias, never to a period-specific index name, so the ILM
// rollover can move writes to a new backing index without this class knowing.
public sealed class ElasticsearchLogWriter : IAppLogWriter
{
    private readonly Storage.StorageRuntime _runtime;
    private readonly DatabaseLoggerOptions _options;
    private readonly Channel<SequencedEntry> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private long _lastSequence;
    private long _dropped;
    private long _written;
    private long _writeFailures;
    private long _lastSuccessfulWriteTicks;
    private long _lastFailureReportTicks;
    private bool _disposed;

    // The client comes from the storage runtime's cached state, which its background loop keeps up
    // to date: resolving services from here would re-enter the container on every log write.
    public ElasticsearchLogWriter(Storage.StorageRuntime runtime, DatabaseLoggerOptions options)
    {
        _runtime = runtime;
        _options = options;

        var capacity = _options.QueueCapacity < 16 ? 16 : _options.QueueCapacity;
        _channel = Channel.CreateBounded<SequencedEntry>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _pump = Task.Run(() => PumpAsync(_shutdown.Token));
    }

    public string Backend => "Elasticsearch";

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public long WrittenCount => Interlocked.Read(ref _written);

    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    public int QueueDepth => _channel.Reader.CanCount ? _channel.Reader.Count : 0;

    public DateTime? LastSuccessfulWriteUtc =>
        Interlocked.Read(ref _lastSuccessfulWriteTicks) is var ticks && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public bool TryEnqueue(AppLogEntry entry)
    {
        if (_disposed)
        {
            return false;
        }

        var sequenced = new SequencedEntry(NextSequence(), entry);
        if (_channel.Writer.TryWrite(sequenced))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    public void Flush() => DrainAndWrite();

    // The dataset's cursor. It plays the role AppLogEntries.Id plays in SQL: strictly increasing
    // within a process, and derived from the wall clock so two hosts on the same machine interleave
    // in time order. Hosts are expected to share a clock (NTP) — with a single-box deployment they
    // trivially do.
    private long NextSequence()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _lastSequence);
            var candidate = Math.Max(DateTime.UtcNow.Ticks, current + 1);
            if (Interlocked.CompareExchange(ref _lastSequence, candidate, current) == current)
            {
                return candidate;
            }
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await _channel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!(_channel.Reader.CanCount && _channel.Reader.Count >= _options.BatchSize))
            {
                try
                {
                    await Task.Delay(_options.FlushInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Fall through and flush what is queued.
                }
            }

            await DrainAndWriteAsync().ConfigureAwait(false);
        }

        await DrainAndWriteAsync().ConfigureAwait(false);
    }

    private void DrainAndWrite()
    {
        DrainAndWriteAsync().GetAwaiter().GetResult();
    }

    private async Task DrainAndWriteAsync()
    {
        var batchSize = _options.BatchSize < 1 ? 1 : _options.BatchSize;

        while (true)
        {
            var batch = new List<SequencedEntry>(batchSize);
            while (batch.Count < batchSize && _channel.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return;
            }

            if (!_runtime.TryGetClient(out var client, out var snapshot))
            {
                // Elasticsearch is switched off, unconfigured, or not yet loaded. Entries are
                // dropped and counted: writing them into the relational log would make the Debug
                // Logs console show a dataset it no longer reads from.
                Interlocked.Increment(ref _writeFailures);
                Interlocked.Add(ref _dropped, batch.Count);
                continue;
            }

            try
            {
                var alias = StorageIndexNaming.WriteAlias(snapshot.IndexPrefix, StorageDatasets.Logs);
                var operations = new List<ElasticsearchBulkOperation>(batch.Count);
                foreach (var item in batch)
                {
                    operations.Add(ElasticsearchBulkOperation.Create(
                        item.Entry.EventId.ToString(),
                        ElasticsearchDocuments.FromLogEntry(item.Entry, item.Sequence)));
                }

                var result = await client.BulkAsync(alias, operations).ConfigureAwait(false);
                if (result.Errors)
                {
                    // A 409 means the same event id was already indexed (a retried batch); that is
                    // not a failure worth reporting.
                    Interlocked.Add(ref _written, result.Created);
                    Interlocked.Increment(ref _writeFailures);
                }
                else
                {
                    Interlocked.Add(ref _written, result.Created);
                }

                Interlocked.Exchange(ref _lastSuccessfulWriteTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _writeFailures);
                Interlocked.Add(ref _dropped, batch.Count);
                ReportSinkFailure(exception, batch.Count);
            }
        }
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
            Console.Error.WriteLine($"fusetor Elasticsearch log sink failure; dropped batch={batchCount}, exception={exception.GetType().Name}: {exception.Message}");
        }
        catch
        {
            // The fallback sink must never affect execution.
        }
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

        _shutdown.Dispose();
    }

    private readonly record struct SequencedEntry(long Sequence, AppLogEntry Entry);
}
