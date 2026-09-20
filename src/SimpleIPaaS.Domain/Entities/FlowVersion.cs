using System;

namespace SimpleIPaaS.Domain.Entities;

public class FlowVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    public Guid TenantId { get; set; }
    public int VersionNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string ChangeNote { get; set; } = string.Empty;
    public int? RolledBackFromVersion { get; set; }
    public string SnapshotJson { get; set; } = string.Empty;
}