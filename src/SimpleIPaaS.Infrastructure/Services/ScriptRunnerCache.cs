using System.Collections.Concurrent;

namespace SimpleIPaaS.Infrastructure.Services;

// Compiled-script cache with a hard size bound and least-recently-used eviction.
//
// The previous caches either grew without limit (the mapping service) or emptied themselves completely at 512
// entries (the advanced service), which meant every hot script was recompiled together right after the wipe -
// a CPU spike on exactly the busiest flows. Eviction here drops only the coldest quarter. Each key is compiled
// once even when many runs ask for it at the same time.
internal sealed class ScriptRunnerCache<TRunner> where TRunner : class
{
    private sealed class Entry
    {
        public Entry(Lazy<TRunner> runner, long usedAt)
        {
            Runner = runner;
            UsedAt = usedAt;
        }

        public Lazy<TRunner> Runner { get; }
        public long UsedAt;
    }

    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, Entry> _items = new();
    private readonly object _trimLock = new();
    private long _clock;

    public ScriptRunnerCache(int capacity) => _capacity = Math.Max(4, capacity);

    public int Count => _items.Count;

    public bool Contains(string key) => _items.ContainsKey(key);

    public TRunner GetOrAdd(string key, Func<TRunner> compile)
    {
        var tick = Interlocked.Increment(ref _clock);
        if (_items.TryGetValue(key, out var existing))
        {
            Volatile.Write(ref existing.UsedAt, tick);
            return existing.Runner.Value;
        }

        var entry = _items.GetOrAdd(key, _ => new Entry(new Lazy<TRunner>(compile, LazyThreadSafetyMode.ExecutionAndPublication), tick));
        Volatile.Write(ref entry.UsedAt, tick);

        TRunner runner;
        try
        {
            runner = entry.Runner.Value;
        }
        catch
        {
            // A script that does not compile must not poison the cache: the next run should report the error again.
            _items.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }

        if (_items.Count > _capacity) Trim();
        return runner;
    }

    private void Trim()
    {
        lock (_trimLock)
        {
            if (_items.Count <= _capacity) return;
            var evict = _items.Count - (_capacity - _capacity / 4);
            foreach (var victim in _items.OrderBy(pair => Volatile.Read(ref pair.Value.UsedAt)).Take(evict).Select(pair => pair.Key).ToArray())
                _items.TryRemove(victim, out _);
        }
    }
}
