using System;

namespace SimpleIPaaS.Domain.Entities;

public class CrossReferenceEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ListName { get; set; } = string.Empty;
    public string KeyValue { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public Guid? FlowId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
