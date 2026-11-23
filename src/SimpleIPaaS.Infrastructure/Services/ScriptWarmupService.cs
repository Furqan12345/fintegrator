using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services;

// Roslyn is by far the slowest cold path in a flow run: the first script compiled in a process pays for loading
// and JIT-compiling the compiler (measured at well over a second), and that cost landed on whichever customer run
// or Debug step happened to come first. This compiles one trivial script of every shape shortly after start,
// in the background, so the first real run starts with a warm compiler. It is best effort: any failure is
// logged and ignored, and it never delays host startup.
public sealed class ScriptWarmupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScriptWarmupService> _logger;

    public ScriptWarmupService(IServiceScopeFactory scopes, ILogger<ScriptWarmupService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the host finish starting and serve its first requests before spending a core on compiling.
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            using var scope = _scopes.CreateScope();
            var scripts = scope.ServiceProvider.GetRequiredService<IAdvancedCodeExecutionService>();
            await scripts.ExecuteMappingAsync("return \"{}\";", "{}", "{}");
            await scripts.ExecuteBranchAsync("return true;", "{}", "{}");
            await scripts.ExecuteUrlAsync("return \"http://localhost/\";", "{}", "{}");
            await scripts.ExecutePostFlightAsync("return \"{}\";", "{}", "{}", "{}");
            await scripts.ExecuteEmailAsync("return new { to = \"warmup@example.test\", subject = \"warmup\", body = \"warmup\" };", "{}", "{}");

            _logger.LogInformation("Script compiler warmed up in {ElapsedMs}ms",
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Script compiler warmup failed; the first real script will compile cold");
        }
    }
}
