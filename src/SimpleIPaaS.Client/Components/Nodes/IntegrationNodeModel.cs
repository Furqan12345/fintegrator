using System;
using Blazor.Diagrams.Core.Models;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Components.Nodes;

public class IntegrationNodeModel : NodeModel
{
    public IntegrationStepDto StepConfig { get; set; }
    public bool IsTestMode { get; set; }
    public string TestStatus { get; set; } = "Runs normally";
    public bool IsMocked { get; set; }
    public Func<IntegrationNodeModel, Task>? TestClick { get; set; }

    public IntegrationNodeModel(IntegrationStepDto stepConfig) : base(new Blazor.Diagrams.Core.Geometry.Point(stepConfig.PositionX, stepConfig.PositionY))
    {
        StepConfig = stepConfig;
        if (stepConfig.StepType == "Branch")
        {
            AddPort(new PortModel("left", this, PortAlignment.Left)); AddPort(new PortModel("true", this, PortAlignment.Right)); AddPort(new PortModel("false", this, PortAlignment.Bottom));
        }
        else if (stepConfig.StepType == "Schedule") AddPort(new PortModel("right", this, PortAlignment.Right));
        else if (stepConfig.StepType == "ForEach")
        {
            AddPort(new PortModel("left", this, PortAlignment.Left)); AddPort(new PortModel("right", this, PortAlignment.Right)); AddPort(new PortModel("completed", this, PortAlignment.Bottom));
        }
        else { AddPort(new PortModel("left", this, PortAlignment.Left)); AddPort(new PortModel("right", this, PortAlignment.Right)); }
    }
}