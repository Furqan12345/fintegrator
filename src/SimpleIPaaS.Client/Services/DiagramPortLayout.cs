using Blazor.Diagrams.Core.Models;

namespace SimpleIPaaS.Client.Services;

public static class DiagramPortLayout
{
    public const string ErrorPortId = "error";

    public static bool IsErrorPort(string? portId) =>
        string.Equals(portId, ErrorPortId, StringComparison.OrdinalIgnoreCase);

    public static PortAlignment OutputAlignment(string? portId) =>
        portId is "false" or "completed" ? PortAlignment.Bottom : PortAlignment.Right;
}
