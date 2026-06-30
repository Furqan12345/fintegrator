using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services;

public class ScriptGlobals
{
    public string InputJson { get; set; } = string.Empty;
}

public class CodeExecutionService : ICodeExecutionService
{
    public async Task<string> ExecuteMappingAsync(string csharpCode, string inputJson)
    {
        try
        {
            // The code provided by the user should be a script that returns a string (the mapped json)
            // e.g. return "{\"hello\": \"world\"}";
            var globals = new ScriptGlobals { InputJson = inputJson };
            
            var options = ScriptOptions.Default
                .AddReferences(
                    typeof(object).Assembly,                           // System.Runtime / mscorlib
                    typeof(System.Linq.Enumerable).Assembly,          // System.Linq
                    typeof(System.Collections.Generic.List<>).Assembly, // System.Collections.Generic
                    typeof(System.Text.Json.JsonDocument).Assembly,   // System.Text.Json
                    typeof(System.Text.Json.Nodes.JsonNode).Assembly, // System.Text.Json.Nodes
                    typeof(Newtonsoft.Json.JsonConvert).Assembly       // Newtonsoft.Json
                )
                .AddImports("System", "System.Linq", "System.Collections.Generic",
                    "System.Text.Json", "System.Text.Json.Nodes",
                    "Newtonsoft.Json", "Newtonsoft.Json.Linq");

            // Execute the script
            var result = await CSharpScript.EvaluateAsync<string>(
                csharpCode, 
                options, 
                globals: globals);
                
            return result ?? string.Empty;
        }
        catch (Exception ex)
        {
            // For MVP, we'll return the error as string to help the user debug
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }
}
