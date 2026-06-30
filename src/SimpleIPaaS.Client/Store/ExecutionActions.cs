using System;
using System.Collections.Generic;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class LoadExecutionsAction { }
public class LoadExecutionsResultAction { public IEnumerable<FlowExecutionDto> Executions { get; set; } = Array.Empty<FlowExecutionDto>(); }

public class LoadExecutionDetailsAction { public Guid ExecutionId { get; set; } }
public class LoadExecutionDetailsResultAction { public IEnumerable<StepExecutionDto> StepExecutions { get; set; } = Array.Empty<StepExecutionDto>(); }
