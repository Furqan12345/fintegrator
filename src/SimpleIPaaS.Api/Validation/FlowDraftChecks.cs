using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Validation;

public static class FlowDraftChecks
{
    private const int MaxTotalScriptChars = 96_000;
    private const int MaxScriptChars = 24_000;

    public static List<string> ValidateCanvasEdges(IntegrationFlowDto flow)
    {
        var byId = flow.Nodes.ToDictionary(node => node.Id);
        var errors = new List<string>();
        foreach (var edge in flow.Edges)
        {
            if (!byId.TryGetValue(edge.SourceNodeId, out var source) || !byId.TryGetValue(edge.TargetNodeId, out var target))
                continue;
            var sourcePortExists = source.StepType switch
            {
                var type when type.Equals("Branch", StringComparison.OrdinalIgnoreCase) => edge.SourcePortId is "true" or "false",
                var type when type.Equals("HttpAction", StringComparison.OrdinalIgnoreCase) =>
                    edge.SourcePortId == "right"
                    || (source.HttpErrorMode.Equals("Continue", StringComparison.OrdinalIgnoreCase) && edge.SourcePortId == "error"),
                var type when type.Equals("Schedule", StringComparison.OrdinalIgnoreCase) => edge.SourcePortId == "right",
                var type when type.Equals("ForEach", StringComparison.OrdinalIgnoreCase) => edge.SourcePortId is "right" or "completed",
                _ => edge.SourcePortId == "right"
            };
            var targetPortExists = !target.StepType.Equals("Schedule", StringComparison.OrdinalIgnoreCase) && edge.TargetPortId == "left";
            if (!sourcePortExists || !targetPortExists)
            {
                var expectedSource = source.StepType.Equals("Branch", StringComparison.OrdinalIgnoreCase)
                    ? "true or false"
                    : source.StepType.Equals("HttpAction", StringComparison.OrdinalIgnoreCase)
                        && source.HttpErrorMode.Equals("Continue", StringComparison.OrdinalIgnoreCase)
                        ? "right or error"
                        : source.StepType.Equals("ForEach", StringComparison.OrdinalIgnoreCase)
                            ? "right or completed"
                            : "right";
                errors.Add($"Edge from '{source.NodeName}' to '{target.NodeName}' uses unsupported ports " +
                    $"(sourcePortId='{edge.SourcePortId}', targetPortId='{edge.TargetPortId}'). " +
                    $"Expected sourcePortId {expectedSource} and targetPortId 'left'.");
            }
        }
        return errors;
    }

    public static List<string> CheckGeneratedScripts(IntegrationFlowDto flow)
    {
        var warnings = new List<string>();
        var scripts = new List<(IntegrationStepDto Node, string Property, string Code, Type ResultType)>();
        foreach (var node in flow.Nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.MappingCode))
            {
                var resultType = node.StepType.Equals("Branch", StringComparison.OrdinalIgnoreCase)
                    ? typeof(bool)
                    : node.StepType.Equals("Email", StringComparison.OrdinalIgnoreCase)
                        ? typeof(object)
                        : typeof(string);
                scripts.Add((node, "MappingCode", node.MappingCode, resultType));
            }
            if (!string.IsNullOrWhiteSpace(node.UrlCode)) scripts.Add((node, "UrlCode", node.UrlCode, typeof(string)));
            if (!string.IsNullOrWhiteSpace(node.PreFlightCode)) scripts.Add((node, "PreFlightCode", node.PreFlightCode, typeof(string)));
            if (!string.IsNullOrWhiteSpace(node.PostFlightCode)) scripts.Add((node, "PostFlightCode", node.PostFlightCode, typeof(string)));
        }
        if (scripts.Sum(script => script.Code.Length) > MaxTotalScriptChars)
            throw new AiFlowBuilderException("Generated C# exceeded the total script size limit. Ask for a smaller flow.");

        var options = ScriptOptions.Default
            .AddReferences(typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(List<>).Assembly,
                typeof(System.Text.Json.JsonDocument).Assembly, typeof(System.Text.Json.Nodes.JsonNode).Assembly,
                typeof(Newtonsoft.Json.JsonConvert).Assembly)
            .AddImports("System", "System.Linq", "System.Collections.Generic", "System.Text.Json", "System.Text.Json.Nodes",
                "Newtonsoft.Json", "Newtonsoft.Json.Linq");

        foreach (var script in scripts)
        {
            if (script.Code.Length > MaxScriptChars)
                throw new AiFlowBuilderException($"Generated C# in '{script.Node.NodeName}' exceeded the per-script size limit.");
            var composedCode = ScriptComposer.Compose(flow.HelperCode, script.Code);
            var diagnostics = script.ResultType == typeof(bool)
                ? CSharpScript.Create<bool>(composedCode, options, typeof(AdvancedScriptGlobals)).Compile()
                : script.ResultType == typeof(object)
                    ? CSharpScript.Create<object>(composedCode, options, typeof(AdvancedScriptGlobals)).Compile()
                    : CSharpScript.Create<string>(composedCode, options, typeof(AdvancedScriptGlobals)).Compile();
            var errors = diagnostics.Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Take(2).Select(diagnostic => diagnostic.GetMessage()).ToList();
            if (errors.Count > 0)
            {
                var label = string.IsNullOrWhiteSpace(script.Node.NodeName) ? script.Node.StepType : script.Node.NodeName;
                warnings.Add($"{label}: generated {script.Property} did not compile yet — {string.Join("; ", errors)}");
            }
            if (warnings.Count >= 8) break;
        }
        return warnings;
    }
}
