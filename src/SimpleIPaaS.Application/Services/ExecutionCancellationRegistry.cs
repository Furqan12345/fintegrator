using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SimpleIPaaS.Application.Services;

public class ExecutionCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationTokenSource GetOrCreate(Guid executionId)
        => _sources.GetOrAdd(executionId, _ => new CancellationTokenSource());

    public bool Cancel(Guid executionId)
    {
        if (_sources.TryGetValue(executionId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            return true;
        }
        return false;
    }

    public void Remove(Guid executionId)
    {
        if (_sources.TryRemove(executionId, out var cts))
        {
            cts.Dispose();
        }
    }
}
