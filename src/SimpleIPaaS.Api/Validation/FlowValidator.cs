using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Validation;

public static class FlowValidator
{
    private const int MaxScriptChars = 24_000;
    private const int MaxFlowScriptChars = 96_000;
    private const int MaxHelperChars = 64_000;

    public static List<string> Validate(IntegrationFlowDto flow)
    {
        var errors = new List<string>();
        if (flow.Nodes.Count == 0)
        {
            errors.Add("A flow must contain at least one node.");
            return errors;
        }

        var totalScriptChars = 0;
        foreach (var node in flow.Nodes)
        {
            if (!Enum.TryParse<SimpleIPaaS.Domain.StepType>(node.StepType, true, out _))
            {
                errors.Add($"Node '{node.NodeName}' has an unknown step type '{node.StepType}'.");
            }

            totalScriptChars += CheckScript(errors, node, "MappingCode", node.MappingCode);
            totalScriptChars += CheckScript(errors, node, "UrlCode", node.UrlCode);
            totalScriptChars += CheckScript(errors, node, "PreFlightCode", node.PreFlightCode);
            totalScriptChars += CheckScript(errors, node, "PostFlightCode", node.PostFlightCode);
        }

        if (totalScriptChars > MaxFlowScriptChars)
        {
            errors.Add($"The flow's C# scripts exceed the {MaxFlowScriptChars:N0}-character total limit.");
        }

        if ((flow.HelperCode?.Length ?? 0) > MaxHelperChars)
        {
            errors.Add($"The helper script exceeds the {MaxHelperChars:N0}-character limit.");
        }

        var duplicateNames = flow.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.NodeName))
            .GroupBy(n => n.NodeName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var name in duplicateNames)
        {
            errors.Add($"Node name '{name}' is used more than once. Node names must be unique.");
        }

        var duplicateIds = flow.Nodes
            .GroupBy(n => n.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var id in duplicateIds)
        {
            errors.Add($"Node id '{id}' is used more than once. Node ids must be unique.");
        }

        if (duplicateIds.Count > 0)
        {
            return errors;
        }

        var nodeIds = flow.Nodes.Select(n => n.Id).ToHashSet();
        foreach (var edge in flow.Edges)
        {
            if (!nodeIds.Contains(edge.SourceNodeId))
            {
                errors.Add($"Edge {edge.Id} references a source node that does not exist.");
            }

            if (!nodeIds.Contains(edge.TargetNodeId))
            {
                errors.Add($"Edge {edge.Id} references a target node that does not exist.");
            }
        }

        if (errors.Count == 0 && HasCycle(flow))
        {
            errors.Add("The flow contains a cycle. Flows must be acyclic.");
        }

        return errors;
    }

    private static int CheckScript(List<string> errors, IntegrationStepDto node, string property, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return 0;
        }

        if (code.Length > MaxScriptChars)
        {
            errors.Add($"Node '{node.NodeName}' {property} exceeds the {MaxScriptChars:N0}-character limit.");
        }

        return code.Length;
    }

    private static bool HasCycle(IntegrationFlowDto flow)
    {
        var adjacency = flow.Nodes.ToDictionary(n => n.Id, _ => new List<Guid>());
        var inDegree = flow.Nodes.ToDictionary(n => n.Id, _ => 0);

        foreach (var edge in flow.Edges)
        {
            adjacency[edge.SourceNodeId].Add(edge.TargetNodeId);
            inDegree[edge.TargetNodeId]++;
        }

        var queue = new Queue<Guid>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            visited++;

            foreach (var target in adjacency[nodeId])
            {
                if (--inDegree[target] == 0)
                {
                    queue.Enqueue(target);
                }
            }
        }

        return visited != flow.Nodes.Count;
    }
}
