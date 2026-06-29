using System;

namespace SimpleIPaaS.Domain.Entities;

public class Connection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    
    public AuthType AuthType { get; set; } = AuthType.None;
    public string AuthConfigJson { get; set; } = string.Empty; // Encrypted JSON blob
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ConnectionStatus Status { get; set; } = ConnectionStatus.Active;
}
