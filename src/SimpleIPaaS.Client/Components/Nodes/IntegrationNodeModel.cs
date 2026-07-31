using Blazor.Diagrams.Core.Models;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Components.Nodes;

public class IntegrationNodeModel : NodeModel
{
    public IntegrationStepDto StepConfig { get; set; }

    public IntegrationNodeModel(IntegrationStepDto stepConfig) : base(new Blazor.Diagrams.Core.Geometry.Point(stepConfig.PositionX, stepConfig.PositionY))
    {
        StepConfig = stepConfig;
        
        // Add default ports for connecting
        if (stepConfig.StepType == "Branch")
        {
            AddPort(new PortModel("left", this, PortAlignment.Left));
            AddPort(new PortModel("true", this, PortAlignment.Right));
            AddPort(new PortModel("false", this, PortAlignment.Bottom));
        }
        else if (stepConfig.StepType == "Schedule")
        {
            AddPort(new PortModel("right", this, PortAlignment.Right));
        }
        else // HttpAction, Mapping, Debug, PersistedState, CrossReferenceStore, CrossReferenceFilter
        {
            AddPort(new PortModel("left", this, PortAlignment.Left));
            AddPort(new PortModel("right", this, PortAlignment.Right));
        }
    }
}
