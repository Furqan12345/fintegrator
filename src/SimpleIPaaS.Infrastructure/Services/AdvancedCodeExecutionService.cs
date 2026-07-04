using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
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
    public async Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        try
        {
            var globals = new AdvancedScriptGlobals
            {
                FlowStateJson = flowStateJson,
                PersistedStateJson = persistedStateJson
            };
            var options = ScriptOptions.Default
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

            var result = await CSharpScript.EvaluateAsync<string>(
                csharpCode, 
                options, 
                globals: globals);
                
            return result ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }

    public async Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson)
    {
        try
        {
            var globals = new AdvancedScriptGlobals 
            { 
                FlowStateJson = flowStateJson,
                PersistedStateJson = persistedStateJson,
                HttpResponseJson = httpResponseJson
            };
            var options = ScriptOptions.Default
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

            var result = await CSharpScript.EvaluateAsync<string>(
                csharpCode, 
                options, 
                globals: globals);
                
            return result ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }

    public async Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        return await ExecuteMappingAsync(csharpCode, flowStateJson, persistedStateJson);
    }

    public async Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson)
    {
        try
        {
            var globals = new AdvancedScriptGlobals
            {
                FlowStateJson = flowStateJson,
                PersistedStateJson = persistedStateJson
            };
            var options = ScriptOptions.Default
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

            var result = await CSharpScript.EvaluateAsync<bool>(
                csharpCode, 
                options, 
                globals: globals);
                
            return result;
        }
        catch
        {
            return false;
        }
    }
}
