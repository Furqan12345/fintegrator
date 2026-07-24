using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Validation;

public static class FlowValidator
{
    public static List<string> Validate(IntegrationFlowDto flow)
    {
        var errors = new List<string>();

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
