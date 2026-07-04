using System;

namespace SimpleIPaaS.Shared.Models;

public class PersistedStateDto
{
    public Guid FlowId { get; set; }
    public string PersistedStateJson { get; set; } = "{}";
}
