using System.Collections.Generic;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

[FeatureState]
public class FlowState
{
    public bool IsLoading { get; }
    public IReadOnlyList<IntegrationFlowDto> Flows { get; }
    public IntegrationFlowDto? CurrentFlow { get; }
    public string RunResult { get; }

    private FlowState() 
    { 
        IsLoading = false;
        Flows = new List<IntegrationFlowDto>();
        CurrentFlow = new IntegrationFlowDto();
        RunResult = string.Empty;
    } 

    public FlowState(bool isLoading, IReadOnlyList<IntegrationFlowDto> flows, IntegrationFlowDto? currentFlow, string runResult)
    {
        IsLoading = isLoading;
        Flows = flows;
        CurrentFlow = currentFlow;
        RunResult = runResult;
    }
}
