using System;

namespace SimpleIPaaS.Shared.Models;

// Debug Mode executes one node at a time from the Flow Designer. The client owns the
// dependency gating; the server only needs the accumulated upstream outputs so the node
// resolves the same input it would receive in a full run.
public class DebugNodeRunRequestDto
{
    public string FlowStateJson { get; set; } = "{}";
    public string? PersistedStateJson { get; set; }
}

public class DebugNodeRunResultDto
{
    public Guid ExecutionId { get; set; }
    public Guid StepId { get; set; }
    public Guid? StepExecutionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public bool BranchResult { get; set; }
    public int HttpStatusCode { get; set; }
    public bool ErrorPort { get; set; }
}
