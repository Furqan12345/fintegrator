using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
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
    // Cooperative cancellation only: scripts must check this, because Roslyn cannot abort
    // code that is already running.
    public CancellationToken CancellationToken { get; set; }
}

public class AdvancedCodeExecutionService : IAdvancedCodeExecutionService
{
    private const int MaxCompiledScripts = 512;
    private static readonly ConcurrentDictionary<string, object> CompiledScripts = new();
    private static readonly ConcurrentDictionary<string, byte> ValidatedHelpers = new();

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
        if (CompiledScripts.Count >= MaxCompiledScripts)
        {
            CompiledScripts.Clear();
        }

        return (ScriptRunner<TResult>)CompiledScripts.GetOrAdd(cacheKey, _ =>
            CSharpScript.Create<TResult>(csharpCode, BuildOptions(), typeof(AdvancedScriptGlobals)).CreateDelegate());
    }

    private async Task<TResult> RunAsync<TResult>(string csharpCode, AdvancedScriptGlobals globals)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            globals.CancellationToken = cts.Token;
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

    public async Task<string> ExecuteMappingAsync(
        string csharpCode,
        string flowStateJson,
        string persistedStateJson,
        string? helperCode = null)
    {
        var globals = new AdvancedScriptGlobals
        {
            FlowStateJson = flowStateJson,
            PersistedStateJson = persistedStateJson
        };

        return await RunAsync<string>(ScriptComposer.Compose(helperCode, csharpCode), globals) ?? string.Empty;
    }

    public async Task<string> ExecuteEmailAsync(
        string csharpCode,
        string flowStateJson,
        string persistedStateJson,
        string? helperCode = null)
    {
        var globals = new AdvancedScriptGlobals
        {
            FlowStateJson = flowStateJson,
            PersistedStateJson = persistedStateJson
        };

        var result = await RunAsync<object>(ScriptComposer.Compose(helperCode, csharpCode), globals);
        return result switch
        {
            null => "{}",
            string text => text,
            Newtonsoft.Json.Linq.JToken token => token.ToString(Newtonsoft.Json.Formatting.None),
            _ => Newtonsoft.Json.JsonConvert.SerializeObject(result)
        };
    }

    public async Task<string> ExecutePostFlightAsync(
        string csharpCode,
        string flowStateJson,
        string persistedStateJson,
        string httpResponseJson,
        string? helperCode = null)
    {
        var globals = new AdvancedScriptGlobals
        {
            FlowStateJson = flowStateJson,
            PersistedStateJson = persistedStateJson,
            HttpResponseJson = httpResponseJson
        };

        return await RunAsync<string>(ScriptComposer.Compose(helperCode, csharpCode), globals) ?? string.Empty;
    }

    public async Task<string> ExecuteUrlAsync(
        string csharpCode,
        string flowStateJson,
        string persistedStateJson,
        string? helperCode = null)
    {
        return await ExecuteMappingAsync(csharpCode, flowStateJson, persistedStateJson, helperCode);
    }

    public async Task<bool> ExecuteBranchAsync(
        string csharpCode,
        string flowStateJson,
        string persistedStateJson,
        string? helperCode = null)
    {
        var globals = new AdvancedScriptGlobals
        {
            FlowStateJson = flowStateJson,
            PersistedStateJson = persistedStateJson
        };

        return await RunAsync<bool>(ScriptComposer.Compose(helperCode, csharpCode), globals);
    }

    public Task ValidateAsync(string csharpCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csharpCode))
        {
            return Task.CompletedTask;
        }

        var cacheKey = ScriptCacheKey.Compute(csharpCode, typeof(AdvancedScriptGlobals), typeof(object));
        if (ValidatedHelpers.ContainsKey(cacheKey))
        {
            return Task.CompletedTask;
        }

        var script = CSharpScript.Create<object>(csharpCode, BuildOptions(), typeof(AdvancedScriptGlobals));
        var errors = script.Compile(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Take(8)
            .Select(FormatDiagnostic)
            .ToList();

        if (errors.Count > 0)
        {
            throw new ScriptExecutionException("Helper script has compile error(s): " + string.Join(" ", errors));
        }

        ValidatedHelpers.TryAdd(cacheKey, 0);
        return Task.CompletedTask;
    }

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource)
        {
            return diagnostic.GetMessage();
        }

        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        return $"line {position.Line + 1}, column {position.Character + 1}: {diagnostic.GetMessage()}";
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

    // JSON-safe single fragment for the stored error message: quotes and control characters stay
    // escaped (the message is embedded as a JSON string), but the default encoder's apostrophe
    // escape (\u0027) is undone — operators saw "The name `\u0027payload\u0027` does not exist in
    // the current context". \u0027 and ' decode to the same character, so this stays valid JSON.
    public static string JsonSafe(string message)
    {
        var encoded = JsonSerializer.Serialize(message ?? string.Empty);
        return encoded.Substring(1, encoded.Length - 2).Replace("\\u0027", "'");
    }
}
