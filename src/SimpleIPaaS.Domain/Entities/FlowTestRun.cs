using System;

namespace SimpleIPaaS.Domain.Entities;

public class FlowTestRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TestCaseId { get; set; }
    public Guid FlowId { get; set; }
    public Guid TenantId { get; set; }
    public Guid FlowExecutionId { get; set; }
    public string Status { get; set; } = "Queued";
    public string ResultJson { get; set; } = "{}";
    public string? ErrorMessage { get; set; }
    public DateTime FlowUpdatedAt { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
