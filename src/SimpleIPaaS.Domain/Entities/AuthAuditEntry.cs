namespace SimpleIPaaS.Domain.Entities;

public class AuthAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Details { get; set; }
}
