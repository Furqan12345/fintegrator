using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Services;

namespace SimpleIPaaS.Tests.Performance;

public sealed class ScriptWarmupTests
{
    private sealed class CountingScripts : IAdvancedCodeExecutionService
    {
        public int Calls;
        public bool Throw;

        private Task<T> Hit<T>(T value)
        {
            Interlocked.Increment(ref Calls);
            if (Throw) throw new InvalidOperationException("boom");
            return Task.FromResult(value);
        }

        public Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) => Hit("{}");
        public Task<string> ExecuteEmailAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) => Hit("{}");
        public Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson, string? helperCode = null) => Hit("{}");
        public Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) => Hit("http://localhost/");
        public Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) => Hit(true);
        public Task ValidateAsync(string csharpCode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ScriptWarmupService Create(CountingScripts scripts)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAdvancedCodeExecutionService>(_ => scripts);
        var provider = services.BuildServiceProvider();
        return new ScriptWarmupService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScriptWarmupService>.Instance);
    }

    [Fact]
    public async Task Warmup_CompilesOneScriptOfEveryShape_InTheBackground()
    {
        var scripts = new CountingScripts();
        var warmup = Create(scripts);

        var started = Stopwatch.StartNew();
        await warmup.StartAsync(CancellationToken.None);
        Assert.True(started.ElapsedMilliseconds < 1500, "StartAsync must not wait for the warm-up");   // never delays host startup

        var watch = Stopwatch.StartNew();
        while (scripts.Calls < 5 && watch.ElapsedMilliseconds < 8000) await Task.Delay(50);
        await warmup.StopAsync(CancellationToken.None);

        Assert.Equal(5, scripts.Calls);   // mapping, branch, url, post-flight, email
    }

    [Fact]
    public async Task Warmup_Failure_IsSwallowed_AndDoesNotBreakHostShutdown()
    {
        var scripts = new CountingScripts { Throw = true };
        var warmup = Create(scripts);

        await warmup.StartAsync(CancellationToken.None);
        var watch = Stopwatch.StartNew();
        while (scripts.Calls < 1 && watch.ElapsedMilliseconds < 8000) await Task.Delay(50);
        await warmup.StopAsync(CancellationToken.None);

        Assert.True(scripts.Calls >= 1);
    }

    [Fact]
    public async Task Warmup_StoppedBeforeItsDelay_DoesNothing()
    {
        var scripts = new CountingScripts();
        var warmup = Create(scripts);

        await warmup.StartAsync(CancellationToken.None);
        await warmup.StopAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(0, scripts.Calls);
    }
}
