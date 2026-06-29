using System;

namespace SimpleIPaaS.Domain.Entities;

public class IntegrationEdge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid FlowId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string SourcePortId { get; set; } = string.Empty;
    public string TargetPortId { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public int Order { get; set; }
}
