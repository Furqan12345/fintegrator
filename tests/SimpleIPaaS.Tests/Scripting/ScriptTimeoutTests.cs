using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Services;

namespace SimpleIPaaS.Tests.Scripting;

public class ScriptTimeoutTests
{
    private const int ConfiguredTimeoutSeconds = 2;
    private const int ScriptDurationMilliseconds = 8000;

    private static AdvancedCodeExecutionService Create(int timeoutSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scripting:TimeoutSeconds"] = timeoutSeconds.ToString()
            })
            .Build();

        return new AdvancedCodeExecutionService(configuration);
    }

    private static Task WarmUpRoslynAsync() =>
        Create(600).ExecuteMappingAsync("return \"roslyn-warmup\";", "{}", "{}");

    [Fact]
    public async Task LongRunningScript_TimesOutAndThrowsWithTimedOutFlag()
    {
        await WarmUpRoslynAsync();

        var service = Create(ConfiguredTimeoutSeconds);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Record.ExceptionAsync(() => service.ExecuteMappingAsync(
            $"await System.Threading.Tasks.Task.Delay({ScriptDurationMilliseconds}); return \"finished\";",
            "{}",
            "{}"));

        stopwatch.Stop();

        Assert.True(exception is ScriptExecutionException,
            $"Expected ScriptExecutionException after the {ConfiguredTimeoutSeconds}s script timeout, but the script " +
            $"ran for {stopwatch.ElapsedMilliseconds}ms and produced {exception?.GetType().Name ?? "no exception"}. " +
            "Scripting:TimeoutSeconds is not enforced once a script starts executing (see architecture section 7.3).");
        Assert.True(((ScriptExecutionException)exception!).TimedOut);
    }
}
