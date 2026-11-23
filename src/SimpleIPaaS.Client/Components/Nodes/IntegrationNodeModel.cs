using System;
using System.Linq;
using Blazor.Diagrams.Core.Models;
using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Components.Nodes;

public class IntegrationNodeModel : NodeModel
{
    public IntegrationStepDto StepConfig { get; set; }
    public bool IsTestMode { get; set; }
    public string TestStatus { get; set; } = "Runs normally";
    public string TestBadge { get; set; } = string.Empty;
    public string TestTone { get; set; } = string.Empty;
    public string TestState { get; set; } = string.Empty;
    public bool IsMocked { get; set; }
    public string? ActiveHttpPort { get; set; }
    public bool IsHttpErrorPortTaken => StepConfig.StepType == "HttpAction" && ActiveHttpPort == "error";
    public Func<IntegrationNodeModel, Task>? TestClick { get; set; }

    // Debug Mode overlays. A node is runnable only when every upstream parent succeeded, so the
    // designer recomputes these flags after each run and after every invalidation.
    public bool IsDebugMode { get; set; }
    public bool DebugRunnable { get; set; }
    public DebugNodeState DebugState { get; set; } = DebugNodeState.Idle;
    public Func<IntegrationNodeModel, Task>? DebugRun { get; set; }
    public Func<IntegrationNodeModel, Task>? DebugSupply { get; set; }
    public Func<IntegrationNodeModel, Task>? DebugViewLogs { get; set; }

    public bool IsDebugRunning => DebugState == DebugNodeState.Running;
    public bool HasDebugLogs => DebugState is DebugNodeState.Success or DebugNodeState.Error;
    public bool HasDebugOutput => DebugState is DebugNodeState.Success or DebugNodeState.Error or DebugNodeState.Supplied;
    public bool IsDebugSupplied => DebugState == DebugNodeState.Supplied;

    // The canvas renders nodes, never the session map, so a completed run has to be projected onto
    // the node or the check badge and the View Logs button never appear.
    public void ApplyDebugSession(DebugNodeSession session)
    {
        DebugState = session.State;
    }

    public IntegrationNodeModel(IntegrationStepDto stepConfig) : base(new Blazor.Diagrams.Core.Geometry.Point(stepConfig.PositionX, stepConfig.PositionY))
    {
        StepConfig = stepConfig;
        if (stepConfig.StepType == "Branch")
        {
            AddPort(new PortModel("left", this, PortAlignment.Left)); AddPort(new PortModel("true", this, PortAlignment.Right)); AddPort(new PortModel("false", this, DiagramPortLayout.OutputAlignment("false")));
        }
        else if (stepConfig.StepType == "HttpAction")
        {
            AddPort(new PortModel("left", this, PortAlignment.Left));
            AddPort(new PortModel("right", this, PortAlignment.Right));
            if (stepConfig.HttpErrorMode == "Continue")
                AddPort(new PortModel(DiagramPortLayout.ErrorPortId, this, DiagramPortLayout.OutputAlignment(DiagramPortLayout.ErrorPortId)));
        }
        else if (stepConfig.StepType == "Schedule") AddPort(new PortModel("right", this, PortAlignment.Right));
        else if (stepConfig.StepType == "ForEach")
        {
            AddPort(new PortModel("left", this, PortAlignment.Left)); AddPort(new PortModel("right", this, PortAlignment.Right)); AddPort(new PortModel("completed", this, DiagramPortLayout.OutputAlignment("completed")));
        }
        else { AddPort(new PortModel("left", this, PortAlignment.Left)); AddPort(new PortModel("right", this, PortAlignment.Right)); }
    }

    public void SyncHttpErrorPort()
    {
        if (StepConfig.StepType != "HttpAction") return;
        var existing = Ports.FirstOrDefault(port => port.Id == "error");
        var shouldShow = StepConfig.HttpErrorMode == "Continue";
        if (shouldShow && existing == null)
            AddPort(new PortModel(DiagramPortLayout.ErrorPortId, this, DiagramPortLayout.OutputAlignment(DiagramPortLayout.ErrorPortId)));
        else if (!shouldShow && existing != null)
            RemovePort(existing);
    }
}
