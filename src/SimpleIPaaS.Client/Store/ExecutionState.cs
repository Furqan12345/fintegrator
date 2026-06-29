using System.Collections.Generic;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

[FeatureState]
public class ExecutionState
{
    public bool IsLoading { get; }
    public IReadOnlyList<FlowExecutionDto> Executions { get; }
    public IReadOnlyList<StepExecutionDto> CurrentStepExecutions { get; }

    private ExecutionState() 
    { 
        IsLoading = false;
        Executions = new List<FlowExecutionDto>();
        CurrentStepExecutions = new List<StepExecutionDto>();
    } 

    public ExecutionState(bool isLoading, IReadOnlyList<FlowExecutionDto> executions, IReadOnlyList<StepExecutionDto> currentStepExecutions)
    {
        IsLoading = isLoading;
        Executions = executions;
        CurrentStepExecutions = currentStepExecutions;
    }
}
