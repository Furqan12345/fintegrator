using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public sealed record FailureAlertEmail(string Subject, string HtmlBody, string TextBody);

// Builds the single digest email for the deployment. Deliberately includes only names, times
// and error messages: never Payload, FlowStateJson or any request/response body.
public sealed class FailureAlertEmailBuilder
{
    private const int MaxEntriesWithLinks = 20;
    private const int MaxErrorLength = 300;
    private const string Footer = "You're receiving this because failure alerts are on in Settings → Alerts.";

    public FailureAlertEmail Build(
        IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)> candidates,
        string? publicBaseUrl)
    {
        var baseUrl = (publicBaseUrl ?? string.Empty).TrimEnd('/');
        var flows = candidates
            .GroupBy(candidate => DisplayFlow(candidate.Entry.FlowName))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subject = candidates.Count == 1
            ? $"Fusetor: 1 failed order in \"{flows[0].Key}\""
            : $"Fusetor: {candidates.Count} failed orders in {flows.Count} flow{(flows.Count == 1 ? string.Empty : "s")}";

        var html = new StringBuilder();
        var text = new StringBuilder();
        html.Append("<p><strong>").Append(Encode(subject)).Append("</strong></p>");
        text.AppendLine(subject).AppendLine();

        var linked = 0;
        var moreCount = Math.Max(0, candidates.Count - MaxEntriesWithLinks);

        foreach (var flow in flows)
        {
            html.Append("<h3>").Append(Encode(flow.Key)).Append("</h3>");
            text.AppendLine(flow.Key);

            foreach (var node in flow
                         .GroupBy(candidate => DisplayNode(candidate.Entry.NodeName))
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entries = node.OrderBy(candidate => candidate.Entry.CreatedAt).ToList();
                var first = entries[0].Entry.CreatedAt;
                var last = entries[^1].Entry.CreatedAt;
                var error = Truncate(entries[0].Entry.ErrorMessage, MaxErrorLength);
                var count = entries.Count;

                html.Append("<p><strong>").Append(Encode(node.Key)).Append("</strong> — ")
                    .Append(count).Append(count == 1 ? " failure" : " failures")
                    .Append(" · first ").Append(first.ToString("yyyy-MM-dd HH:mm 'UTC'"))
                    .Append(" · last ").Append(last.ToString("yyyy-MM-dd HH:mm 'UTC'"))
                    .Append("</p>");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    html.Append("<p>").Append(Encode(error)).Append("</p>");
                }

                text.AppendLine($"  {node.Key} — {count} failure(s); first {first:yyyy-MM-dd HH:mm} UTC; last {last:yyyy-MM-dd HH:mm} UTC");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    text.AppendLine("    " + error);
                }

                var remaining = Math.Max(0, MaxEntriesWithLinks - linked);
                var toLink = entries.Take(remaining).ToList();
                if (toLink.Count > 0)
                {
                    html.Append("<ul>");
                    foreach (var candidate in toLink)
                    {
                        linked++;
                        var label = candidate.Entry.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'");
                        var link = BuildEntryLink(baseUrl, candidate.Entry.Id);
                        html.Append("<li>");
                        if (link == null)
                        {
                            html.Append(Encode(label));
                        }
                        else
                        {
                            html.Append("<a href=\"").Append(Encode(link)).Append("\">")
                                .Append(Encode(label)).Append("</a>");
                        }
                        html.Append("</li>");
                    }
                    html.Append("</ul>");

                    text.AppendLine("    " + string.Join(", ", toLink.Select(candidate =>
                        BuildEntryLink(baseUrl, candidate.Entry.Id)
                        ?? candidate.Entry.CreatedAt.ToString("yyyy-MM-dd HH:mm") + " UTC")));
                }
            }
        }

        if (moreCount > 0)
        {
            var moreLink = BuildAllLink(baseUrl, candidates);
            html.Append("<p>").Append(Encode($"…and {moreCount} more."));
            if (moreLink != null)
            {
                html.Append(" <a href=\"").Append(Encode(moreLink)).Append("\">Open all</a>");
            }
            html.Append("</p>");
            text.AppendLine($"…and {moreCount} more." + (moreLink == null ? string.Empty : " " + moreLink));
        }

        html.Append("<p style=\"color:#666;font-size:12px\">").Append(Encode(Footer)).Append("</p>");
        text.AppendLine().AppendLine(Footer);

        return new FailureAlertEmail(subject, html.ToString(), text.ToString());
    }

    private static string? BuildEntryLink(string baseUrl, Guid entryId) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl}/deadletters?id={entryId}";

    private static string? BuildAllLink(
        string baseUrl,
        IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)> candidates)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || candidates.Count == 0)
        {
            return null;
        }

        // A flow/step-specific link is only safe when every entry in the digest shares that
        // scope. Otherwise the "Open all" link would silently hide entries from other flows.
        if (candidates.Any(candidate => !candidate.FlowId.HasValue))
        {
            return $"{baseUrl}/deadletters";
        }

        var flowIds = candidates.Select(candidate => candidate.FlowId!.Value).Distinct().ToList();
        if (flowIds.Count != 1)
        {
            return $"{baseUrl}/deadletters";
        }

        var stepIds = candidates.Select(candidate => candidate.Entry.StepId).Distinct().ToList();
        return stepIds.Count == 1 && stepIds[0] != Guid.Empty
            ? $"{baseUrl}/deadletters?flowId={flowIds[0]}&stepId={stepIds[0]}"
            : $"{baseUrl}/deadletters?flowId={flowIds[0]}";
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string DisplayFlow(string? flowName) =>
        string.IsNullOrWhiteSpace(flowName) ? "(unnamed flow)" : flowName;

    private static string DisplayNode(string? nodeName) =>
        string.IsNullOrWhiteSpace(nodeName) ? "(unnamed node)" : nodeName;

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value[..max] + "…";
}
