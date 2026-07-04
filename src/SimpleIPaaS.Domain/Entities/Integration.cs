using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Domain.Entities;

public class Integration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<IntegrationFlow> Flows { get; set; } = new List<IntegrationFlow>();
}
