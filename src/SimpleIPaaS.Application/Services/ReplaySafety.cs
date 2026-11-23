using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

// One place decides which replayed nodes have external side effects. Email and
// POST/PATCH-like HTTP calls are unsafe unless the flow opts into post replay.
public static class ReplaySafety
{
    private static readonly string[] SafeMethods = { "GET", "PUT", "DELETE" };

    public static bool IsReplayAllowed(string? method, bool allowPostReplay) =>
        SafeMethods.Contains(method, StringComparer.OrdinalIgnoreCase) || allowPostReplay;

    public static bool IsUnsafeReplayNode(IntegrationStep node, bool allowPostReplay) =>
        (node.StepType == StepType.Email && !allowPostReplay) ||
        (node.StepType == StepType.HttpAction && !IsReplayAllowed(node.HttpMethod, allowPostReplay));
}
