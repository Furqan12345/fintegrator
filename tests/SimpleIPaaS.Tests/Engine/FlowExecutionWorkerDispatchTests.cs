using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Engine.Workers;

namespace SimpleIPaaS.Tests.Engine;

// The execution worker used to run one polling loop per worker (4 claim queries per interval while idle).
// It now runs a single poller gated by a free-slot semaphore: these tests pin the contract that matters -
// an idle Engine asks for work once per interval, a burst is drained without waiting for the interval, and
// the concurrency cap still holds.
public sealed class FlowExecutionWorkerDispatchTests
{
    // A repository that only knows the two calls the worker makes before it hands off to the executor.
    // Everything else is a test bug and throws.
    private class FakeRepository : DispatchProxy
    {
        public ConcurrentQueue<QueuedExecutionClaim> Queue = new();
        public int ClaimQueries;
        public int Running;
        public int MaxRunning;
        public int Processed;
        public int HoldMilliseconds;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IExecutionRepository.TryClaimNextQueuedExecutionAsync):
                    Interlocked.Increment(ref ClaimQueries);
                    return Task.FromResult(Queue.TryDequeue(out var claim) ? claim : null);

                case nameof(IExecutionRepository.GetFlowExecutionAsync):
                    return Hold();

                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }

        private async Task<FlowExecution?> Hold()
        {
            var now = Interlocked.Increment(ref Running);
            int seen;
            while ((seen = Volatile.Read(ref MaxRunning)) < now && Interlocked.CompareExchange(ref MaxRunning, now, seen) != seen) { }
            if (HoldMilliseconds > 0) await Task.Delay(HoldMilliseconds);
            Interlocked.Decrement(ref Running);
            Interlocked.Increment(ref Processed);
            return null; // "not found; skipping" - the worker returns without needing a FlowExecutor
        }
    }

    private static (FlowExecutionWorker Worker, FakeRepository Repository) Create(int concurrency, int pollSeconds, int holdMs = 0)
    {
        var repository = DispatchProxy.Create<IExecutionRepository, FakeRepository>();
        var fake = (FakeRepository)(object)repository;
        fake.HoldMilliseconds = holdMs;

        var services = new ServiceCollection();
        services.AddScoped(_ => repository);
        var provider = services.BuildServiceProvider();

        var worker = new FlowExecutionWorker(
            provider,
            new ExecutionCancellationRegistry(),
            Options.Create(new ExecutionOptions { MaxConcurrency = concurrency, PollIntervalSeconds = pollSeconds }),
            NullLogger<FlowExecutionWorker>.Instance);
        return (worker, fake);
    }

    private static void Enqueue(FakeRepository repository, int count)
    {
        for (var i = 0; i < count; i++)
            repository.Queue.Enqueue(new QueuedExecutionClaim(Guid.NewGuid(), Guid.NewGuid(), "Test", null));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
    }

    [Fact]
    public async Task IdleEngine_RunsOneClaimQuery_PerPollInterval_NotOnePerWorker()
    {
        var (worker, repository) = Create(concurrency: 4, pollSeconds: 1);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(2500);
        await worker.StopAsync(CancellationToken.None);

        // Polls at t=0, 1, 2 -> 3 queries. One loop per worker would have issued 12.
        Assert.InRange(repository.ClaimQueries, 2, 5);
    }

    [Fact]
    public async Task Burst_IsDrainedImmediately_WithoutWaitingForTheNextPollInterval()
    {
        var (worker, repository) = Create(concurrency: 4, pollSeconds: 30);
        Enqueue(repository, 10);

        var watch = Stopwatch.StartNew();
        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => repository.Processed == 10, 5000);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(10, repository.Processed);
        Assert.True(watch.ElapsedMilliseconds < 5000, "the burst waited for the 30 s poll interval");
    }

    [Fact]
    public async Task Concurrency_IsCapped_AtTheConfiguredWorkerCount_AndUsesAllOfIt()
    {
        var (worker, repository) = Create(concurrency: 3, pollSeconds: 30, holdMs: 200);
        Enqueue(repository, 12);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => repository.Processed == 12, 10000);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(12, repository.Processed);
        Assert.Equal(3, repository.MaxRunning);
    }

    [Fact]
    public async Task Stop_WaitsForExecutionsThatAreStillRunning()
    {
        var (worker, repository) = Create(concurrency: 2, pollSeconds: 30, holdMs: 400);
        Enqueue(repository, 2);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => repository.Running == 2, 3000);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, repository.Processed);
        Assert.Equal(0, repository.Running);
    }
}
