using System;
using System.Collections.Generic;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class LoadExecutionsAction
{
    public Guid? FlowId { get; set; }
    public Guid? IntegrationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public DateTime? SinceUtc { get; set; }
    public DateTime? UntilUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public bool IncludeTests { get; set; }
}
public class LoadExecutionsResultAction { public IEnumerable<FlowExecutionDto> Executions { get; set; } = Array.Empty<FlowExecutionDto>(); }

public class LoadExecutionDetailsAction { public Guid ExecutionId { get; set; } }
public class LoadExecutionDetailsResultAction { public IEnumerable<StepExecutionDto> StepExecutions { get; set; } = Array.Empty<StepExecutionDto>(); }

public class CancelExecutionAction
{
    public Guid Id { get; set; }
    public Guid? FlowId { get; set; }
    public Guid? IntegrationId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public DateTime? SinceUtc { get; set; }
    public DateTime? UntilUtc { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public bool IncludeTests { get; set; }
}
