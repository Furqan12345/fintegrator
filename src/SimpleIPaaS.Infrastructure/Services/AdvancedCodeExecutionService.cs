using System;
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

    private string TimeoutError() =>
        JsonSerializer.Serialize(new { error = $"Script execution timed out after {_timeout.TotalSeconds} seconds" });

    public async Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            var globals = new AdvancedScriptGlobals
            {
                FlowStateJson = flowStateJson,
                PersistedStateJson = persistedStateJson
            };

            var result = await CSharpScript.EvaluateAsync<string>(
                csharpCode,
                BuildOptions(),
                globals: globals,
                cancellationToken: cts.Token);

            return result ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            return TimeoutError();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public async Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            var globals = new AdvancedScriptGlobals
            {
                FlowStateJson = flowStateJson,
                PersistedStateJson = persistedStateJson,
                HttpResponseJson = httpResponseJson
            };

            var result = await CSharpScript.EvaluateAsync<string>(
                csharpCode,
                BuildOptions(),
                globals: globals,
                cancellationToken: cts.Token);

            return result ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            return TimeoutError();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public async Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        return await ExecuteMappingAsync(csharpCode, flowStateJson, persistedStateJson);
    }

    public async Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            var globals = new AdvancedScriptGlobals
            {
                FlowStateJson = flowStateJson,
                PersistedStateJson = persistedStateJson
            };

            var result = await CSharpScript.EvaluateAsync<bool>(
                csharpCode,
                BuildOptions(),
                globals: globals,
                cancellationToken: cts.Token);

            return result;
        }
        catch
        {
            return false;
        }
    }
}
