using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services;

public class AdvancedScriptGlobals
{
    public string FlowStateJson { get; set; } = "{}";
    public string PersistedStateJson { get; set; } = "{}";
    public string HttpResponseJson { get; set; } = string.Empty;
}

public class AdvancedCodeExecutionService : IAdvancedCodeExecutionService
{
    private static readonly ConcurrentDictionary<string, object> CompiledScripts = new();

    private readonly TimeSpan _timeout;

    public AdvancedCodeExecutionService(IConfiguration configuration)
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

    private static ScriptRunner<TResult> GetRunner<TResult>(string csharpCode)
    {
        var cacheKey = ScriptCacheKey.Compute(csharpCode, typeof(AdvancedScriptGlobals), typeof(TResult));
        return (ScriptRunner<TResult>)CompiledScripts.GetOrAdd(cacheKey, _ =>
            CSharpScript.Create<TResult>(csharpCode, BuildOptions(), typeof(AdvancedScriptGlobals)).CreateDelegate());
    }

    private async Task<TResult> RunAsync<TResult>(string csharpCode, AdvancedScriptGlobals globals)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            var runner = GetRunner<TResult>(csharpCode);
            var scriptTask = Task.Run(() => runner(globals, cts.Token));
            var completed = await Task.WhenAny(scriptTask, Task.Delay(_timeout, cts.Token));

            if (completed != scriptTask)
            {
                cts.Cancel();
                throw new ScriptExecutionException(
                    $"Script execution timed out after {_timeout.TotalSeconds} seconds", timedOut: true);
            }

            return await scriptTask;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new ScriptExecutionException(
                $"Script execution timed out after {_timeout.TotalSeconds} seconds", timedOut: true);
        }
        catch (ScriptExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ScriptExecutionException(ScriptCacheKey.JsonSafe(ex.Message), timedOut: false, innerException: ex);
        }
    }

    public async Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        var globals = new AdvancedScriptGlobals
        {
            FlowStateJson = flowStateJson,
            PersistedStateJson = persistedStateJson
        };

        return await RunAsync<string>(csharpCode, globals) ?? string.Empty;
    }

    public async Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson)
    {
        var globals = new AdvancedScriptGlobals
        {
            FlowStateJson = flowStateJson,
            PersistedStateJson = persistedStateJson,
            HttpResponseJson = httpResponseJson
        };

        return await RunAsync<string>(csharpCode, globals) ?? string.Empty;
    }

    public async Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        return await ExecuteMappingAsync(csharpCode, flowStateJson, persistedStateJson);
    }

    public async Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        var globals = new AdvancedScriptGlobals
        {
            FlowStateJson = flowStateJson,
            PersistedStateJson = persistedStateJson
        };

        return await RunAsync<bool>(csharpCode, globals);
    }
}

internal static class ScriptCacheKey
{
    public static string Compute(string csharpCode, Type globalsType, Type resultType)
    {
        var material = $"{globalsType.FullName}|{resultType.FullName}|{csharpCode}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    public static string JsonSafe(string message)
    {
        var encoded = JsonSerializer.Serialize(message ?? string.Empty);
        return encoded.Substring(1, encoded.Length - 2);
    }
}
