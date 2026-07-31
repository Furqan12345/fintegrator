using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class CrossReferenceListDto
{
    public Guid Id { get; set; } = Guid.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CrossReferenceEntryDto
{
    public Guid Id { get; set; } = Guid.Empty;
    public string ListName { get; set; } = string.Empty;
    public string KeyValue { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public Guid? FlowId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CrossReferenceCommandResultDto
{
    public string ListName { get; set; } = string.Empty;
    public int Removed { get; set; }
}

public class CrossReferenceEntryPageDto
{
    public List<CrossReferenceEntryDto> Entries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
