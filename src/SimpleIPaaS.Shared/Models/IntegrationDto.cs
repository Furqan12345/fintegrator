using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class IntegrationDto
{
    public Guid Id { get; set; } = Guid.Empty;
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<IntegrationFlowDto> Flows { get; set; } = new();
}
