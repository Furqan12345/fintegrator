namespace SimpleIPaaS.Application.Models;

public sealed class OperationsOptions
{
    public string AlertWebhookUrl { get; set; } = string.Empty;
    public bool AlertOnExecutionFailure { get; set; } = true;
    public int EngineHeartbeatIntervalSeconds { get; set; } = 15;
    public int EngineHeartbeatTimeoutSeconds { get; set; } = 90;
}