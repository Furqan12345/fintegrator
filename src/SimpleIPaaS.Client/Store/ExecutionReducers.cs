using System.Linq;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public static class ExecutionReducers
{
    [ReducerMethod]
    public static ExecutionState ReduceLoadExecutionsAction(ExecutionState state, LoadExecutionsAction action) =>
        new ExecutionState(isLoading: true, executions: state.Executions, currentStepExecutions: state.CurrentStepExecutions);

    [ReducerMethod]
    public static ExecutionState ReduceLoadExecutionsResultAction(ExecutionState state, LoadExecutionsResultAction action) =>
        new ExecutionState(isLoading: false, executions: action.Executions.ToList(), currentStepExecutions: state.CurrentStepExecutions);

    [ReducerMethod]
    public static ExecutionState ReduceLoadExecutionDetailsAction(ExecutionState state, LoadExecutionDetailsAction action) =>
        new ExecutionState(isLoading: true, executions: state.Executions, currentStepExecutions: state.CurrentStepExecutions);

    [ReducerMethod]
    public static ExecutionState ReduceLoadExecutionDetailsResultAction(ExecutionState state, LoadExecutionDetailsResultAction action) =>
        new ExecutionState(isLoading: false, executions: state.Executions, currentStepExecutions: action.StepExecutions.ToList());

    [ReducerMethod]
    public static ExecutionState ReduceCancelExecutionAction(ExecutionState state, CancelExecutionAction action) =>
        new ExecutionState(isLoading: true, executions: state.Executions, currentStepExecutions: state.CurrentStepExecutions);
}
