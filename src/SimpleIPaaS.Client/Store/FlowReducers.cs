using System.Linq;
using Fluxor;

namespace SimpleIPaaS.Client.Store;

public static class FlowReducers
{
    [ReducerMethod]
    public static FlowState ReduceLoadFlowAction(FlowState state, LoadFlowAction action) =>
        new FlowState(isLoading: true, flows: state.Flows, currentFlow: state.CurrentFlow, runResult: state.RunResult, lastExecutionId: state.LastExecutionId);

    [ReducerMethod]
    public static FlowState ReduceLoadFlowResultAction(FlowState state, LoadFlowResultAction action) =>
        new FlowState(isLoading: false, flows: state.Flows, currentFlow: action.Flow, runResult: state.RunResult, lastExecutionId: state.LastExecutionId);

    [ReducerMethod]
    public static FlowState ReduceLoadFlowsAction(FlowState state, LoadFlowsAction action) =>
        new FlowState(isLoading: true, flows: state.Flows, currentFlow: state.CurrentFlow, runResult: state.RunResult, lastExecutionId: state.LastExecutionId);

    [ReducerMethod]
    public static FlowState ReduceLoadFlowsResultAction(FlowState state, LoadFlowsResultAction action) =>
        new FlowState(isLoading: false, flows: action.Flows.ToList(), currentFlow: state.CurrentFlow, runResult: state.RunResult, lastExecutionId: state.LastExecutionId);

    [ReducerMethod]
    public static FlowState ReduceSaveFlowAction(FlowState state, SaveFlowAction action) =>
        new FlowState(isLoading: true, flows: state.Flows, currentFlow: state.CurrentFlow, runResult: state.RunResult, lastExecutionId: state.LastExecutionId);

    [ReducerMethod]
    public static FlowState ReduceSaveFlowResultAction(FlowState state, SaveFlowResultAction action) =>
        new FlowState(isLoading: false, flows: state.Flows, currentFlow: action.Flow, runResult: state.RunResult, lastExecutionId: state.LastExecutionId);

    [ReducerMethod]
    public static FlowState ReduceRunFlowAction(FlowState state, RunFlowAction action) =>
        new FlowState(isLoading: true, flows: state.Flows, currentFlow: state.CurrentFlow, runResult: "Running...", lastExecutionId: null);

    [ReducerMethod]
    public static FlowState ReduceRunFlowResultAction(FlowState state, RunFlowResultAction action) =>
        new FlowState(isLoading: false, flows: state.Flows, currentFlow: state.CurrentFlow, runResult: action.Result, lastExecutionId: action.ExecutionId);
        
    [ReducerMethod]
    public static FlowState ReduceUpdateFlowStateAction(FlowState state, UpdateFlowStateAction action) =>
        new FlowState(isLoading: state.IsLoading, flows: state.Flows, currentFlow: action.Flow, runResult: state.RunResult, lastExecutionId: state.LastExecutionId);
}
