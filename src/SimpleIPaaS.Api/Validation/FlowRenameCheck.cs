using System.Text.RegularExpressions;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Validation;

// A card's output lives in the flow state under the card's name, so renaming a card silently breaks every
// script or path that still reads flowState.OldName: the value is simply empty at run time. A save is not
// blocked for this (the references may be updated in the next save); it returns a warning per renamed card.
public static class FlowRenameCheck
{
    public static List<string> FindStaleReferences(IntegrationFlow stored, IntegrationFlowDto updated)
    {
        var warnings = new List<string>();
        var storedNames = stored.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.NodeName))
            .ToDictionary(node => node.Id, node => node.NodeName.Trim());
        var currentNames = updated.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.NodeName))
            .Select(node => node.NodeName.Trim())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in updated.Nodes)
        {
            if (!storedNames.TryGetValue(node.Id, out var oldName))
            {
                continue;
            }

            var newName = (node.NodeName ?? string.Empty).Trim();
            // Unchanged, or the old name now belongs to another card (names swapped): references still resolve.
            if (string.Equals(oldName, newName, StringComparison.Ordinal) || currentNames.Contains(oldName))
            {
                continue;
            }

            var pattern = ReferencePattern(oldName);
            var places = new List<string>();
            foreach (var candidate in updated.Nodes)
            {
                var label = string.IsNullOrWhiteSpace(candidate.NodeName) ? candidate.StepType : candidate.NodeName.Trim();
                if (Texts(candidate).Any(text => pattern.IsMatch(text)))
                {
                    places.Add($"'{label}'");
                }
            }

            if (!string.IsNullOrEmpty(updated.HelperCode) && pattern.IsMatch(updated.HelperCode))
            {
                places.Add("the helper script");
            }

            if (places.Count > 0)
            {
                warnings.Add(
                    $"Card '{oldName}' was renamed to '{newName}', but {string.Join(", ", places)} still {(places.Count == 1 ? "refers" : "refer")} to '{oldName}'. " +
                    $"Update those references to '{newName}', or they will be empty when the flow runs.");
            }
        }

        return warnings;
    }

    // flowState.Name (not followed by more name characters) or ["Name"] / ['Name'].
    private static Regex ReferencePattern(string name)
    {
        var escaped = Regex.Escape(name);
        return new Regex(
            $@"(?<![\w$])flowState\.{escaped}(?![\w$])|\[\s*[""']{escaped}[""']\s*\]",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static IEnumerable<string> Texts(IntegrationStepDto node)
    {
        foreach (var text in new[] { node.MappingCode, node.UrlCode, node.PreFlightCode, node.PostFlightCode, node.StepConfig, node.EndpointUrl })
        {
            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }
}
