using System;
using System.Text.Json;
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
    private readonly TimeSpan _timeout;

    public CodeExecutionService(IConfiguration configuration)
    {
        _timeout = TimeSpan.FromSeconds(
            int.TryParse(configuration["Scripting:TimeoutSeconds"], out var seconds) && seconds > 0 ? seconds : 10);
    }

    public async Task<string> ExecuteMappingAsync(string csharpCode, string inputJson)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            var globals = new ScriptGlobals { InputJson = inputJson };

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
                globals: globals,
                cancellationToken: cts.Token);

            return result ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { error = $"Script execution timed out after {_timeout.TotalSeconds} seconds" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
