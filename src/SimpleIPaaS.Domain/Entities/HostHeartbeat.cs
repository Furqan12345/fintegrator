using System;

namespace SimpleIPaaS.Domain.Entities;

public class HostHeartbeat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ServiceName { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Healthy";
}