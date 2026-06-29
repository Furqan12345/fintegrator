using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Domain.Entities;

public class IntegrationFlow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    
    public FlowStatus Status { get; set; } = FlowStatus.Draft;
    public TriggerType TriggerType { get; set; } = TriggerType.Manual;
    public string CronExpression { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    public ICollection<IntegrationStep> Nodes { get; set; } = new List<IntegrationStep>();
    public ICollection<IntegrationEdge> Edges { get; set; } = new List<IntegrationEdge>();
}
