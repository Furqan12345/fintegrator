using System;

namespace SimpleIPaaS.Domain.Entities;

// Host-wide failure-alert configuration: one deployment belongs to one customer, so there is
// exactly one row (Id = 1). Recipients are stored as the owner typed them (comma or semicolon
// separated); the API validates each address before saving.
public sealed class AlertSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Recipients { get; set; } = string.Empty;
    public int CooldownMinutes { get; set; } = 15;

    // Failures created before this moment are never emailed, so enabling alerts does not
    // send the whole existing backlog. Reset whenever Enabled flips false -> true.
    public DateTime? EnabledAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
