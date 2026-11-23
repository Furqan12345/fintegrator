using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SimpleIPaaS.Infrastructure.Services;

namespace SimpleIPaaS.Tests.Performance;

public sealed class ScriptRunnerCacheTests
{
    private sealed class Runner
    {
        public Runner(string name) => Name = name;
        public string Name { get; }
    }

    [Fact]
    public void Cache_NeverGrowsPastItsCapacity_AndEvictsTheColdestEntriesFirst()
    {
        var cache = new ScriptRunnerCache<Runner>(16);
        var compiled = 0;
        Runner Compile(string key) => cache.GetOrAdd(key, () => { Interlocked.Increment(ref compiled); return new Runner(key); });

        for (var i = 0; i < 16; i++) Compile("k" + i);
        Assert.Equal(16, cache.Count);

        for (var i = 16; i < 40; i++) Compile("k" + i);

        Assert.True(cache.Count <= 16, $"cache holds {cache.Count} entries, capacity is 16");
        Assert.True(cache.Contains("k39"));    // newest survives
        Assert.False(cache.Contains("k0"));    // the oldest entry was evicted
        Assert.True(compiled >= 40);           // every distinct key compiled once
    }

    [Fact]
    public void HotScripts_AreNotRecompiled_WhenTheCacheOverflows()
    {
        var cache = new ScriptRunnerCache<Runner>(16);
        var compilesOfHot = 0;
        Runner Hot() => cache.GetOrAdd("hot", () => { Interlocked.Increment(ref compilesOfHot); return new Runner("hot"); });

        for (var i = 0; i < 200; i++)
        {
            Hot();
            cache.GetOrAdd("cold" + i, () => new Runner("cold" + i));
        }

        // The old cache cleared itself completely at its limit, recompiling the hot script every time.
        Assert.Equal(1, compilesOfHot);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameScript_CompileItOnce()
    {
        var cache = new ScriptRunnerCache<Runner>(16);
        var compiles = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            cache.GetOrAdd("same", () =>
            {
                Interlocked.Increment(ref compiles);
                Thread.Sleep(30);
                return new Runner("same");
            }))));

        Assert.Equal(1, compiles);
        Assert.All(results, runner => Assert.Same(results[0], runner));
    }

    [Fact]
    public void ScriptThatFailsToCompile_IsNotCached_SoTheErrorIsReportedAgain()
    {
        var cache = new ScriptRunnerCache<Runner>(16);
        var attempts = 0;
        Runner Failing() => cache.GetOrAdd("bad", () => { attempts++; throw new InvalidOperationException("compile error"); });

        Assert.Throws<InvalidOperationException>(Failing);
        Assert.Throws<InvalidOperationException>(Failing);
        Assert.Equal(2, attempts);
        Assert.False(cache.Contains("bad"));
    }

    [Fact]
    public async Task Services_StillCompileAndRunScripts_AfterTheCacheRewrite()
    {
        var configuration = new ConfigurationBuilder().Build();
        var advanced = new AdvancedCodeExecutionService(configuration);
        var first = await advanced.ExecuteMappingAsync("return \"a\" + 1;", "{}", "{}");
        var again = await advanced.ExecuteMappingAsync("return \"a\" + 1;", "{}", "{}");
        Assert.Equal(first, again);

        var simple = new CodeExecutionService(configuration);
        Assert.Equal("{\"x\":1}", (await simple.ExecuteMappingAsync("return \"{\\\"x\\\":1}\";", "{}")).Replace(" ", ""));
    }
}
