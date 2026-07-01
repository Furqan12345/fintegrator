using System;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class LoadFlowAction { public Guid Id { get; set; } }
public class LoadFlowResultAction { public IntegrationFlowDto Flow { get; set; } = null!; }
public class LoadFlowsAction { }
public class LoadFlowsResultAction { public IEnumerable<IntegrationFlowDto> Flows { get; set; } = Array.Empty<IntegrationFlowDto>(); }
public class SaveFlowAction
{
    public IntegrationFlowDto Flow { get; set; } = null!;
    public bool RunAfterSave { get; set; }
}
public class SaveFlowResultAction { public IntegrationFlowDto Flow { get; set; } = null!; }
public class RunFlowAction { public Guid Id { get; set; } }
public class RunFlowResultAction
{
    public string Result { get; set; } = string.Empty;
    public Guid? ExecutionId { get; set; }
}
public class UpdateFlowStateAction { public IntegrationFlowDto Flow { get; set; } = null!; }
