using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services;

public class ScriptGlobals
{
    public string InputJson { get; set; } = string.Empty;
}

public class CodeExecutionService : ICodeExecutionService
{
    private static readonly ConcurrentDictionary<string, ScriptRunner<string>> CompiledScripts = new();

    private readonly TimeSpan _timeout;

    public CodeExecutionService(IConfiguration configuration)
    {
        _timeout = TimeSpan.FromSeconds(
            int.TryParse(configuration["Scripting:TimeoutSeconds"], out var seconds) && seconds > 0 ? seconds : 10);
    }

    private static ScriptOptions BuildOptions()
    {
        return ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(System.Linq.Enumerable).Assembly,
                typeof(System.Collections.Generic.List<>).Assembly,
                typeof(System.Text.Json.JsonDocument).Assembly,
                typeof(System.Text.Json.Nodes.JsonNode).Assembly,
                typeof(Newtonsoft.Json.JsonConvert).Assembly
            )
            .AddImports("System", "System.Linq", "System.Collections.Generic",
                "System.Text.Json", "System.Text.Json.Nodes",
                "Newtonsoft.Json", "Newtonsoft.Json.Linq");
    }

    private static ScriptRunner<string> GetRunner(string csharpCode)
    {
        var cacheKey = ScriptCacheKey.Compute(csharpCode, typeof(ScriptGlobals), typeof(string));
        return CompiledScripts.GetOrAdd(cacheKey, _ =>
            CSharpScript.Create<string>(csharpCode, BuildOptions(), typeof(ScriptGlobals)).CreateDelegate());
    }

    public async Task<string> ExecuteMappingAsync(string csharpCode, string inputJson)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            var runner = GetRunner(csharpCode);
            var scriptTask = Task.Run(() => runner(new ScriptGlobals { InputJson = inputJson }, cts.Token));
            var completed = await Task.WhenAny(scriptTask, Task.Delay(_timeout, cts.Token));

            if (completed != scriptTask)
            {
                cts.Cancel();
                throw new ScriptExecutionException(
                    $"Script execution timed out after {_timeout.TotalSeconds} seconds", timedOut: true);
            }

            return await scriptTask ?? string.Empty;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new ScriptExecutionException(
                $"Script execution timed out after {_timeout.TotalSeconds} seconds", timedOut: true);
        }
        catch (Exception ex)
        {
            throw new ScriptExecutionException(ScriptCacheKey.JsonSafe(ex.Message), timedOut: false, innerException: ex);
        }
    }
}
