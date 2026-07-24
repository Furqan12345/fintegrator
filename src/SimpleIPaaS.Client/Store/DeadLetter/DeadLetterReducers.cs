using System.Linq;
using Fluxor;

namespace SimpleIPaaS.Client.Store;

public static class DeadLetterReducers
{
    [ReducerMethod]
    public static DeadLetterState ReduceLoadDeadLettersAction(DeadLetterState state, LoadDeadLettersAction action) =>
        new DeadLetterState(isLoading: true, deadLetters: state.DeadLetters, statusFilter: action.Status);

    [ReducerMethod]
    public static DeadLetterState ReduceLoadDeadLettersResultAction(DeadLetterState state, LoadDeadLettersResultAction action) =>
        new DeadLetterState(isLoading: false, deadLetters: action.DeadLetters.ToList(), statusFilter: state.StatusFilter);

    [ReducerMethod]
    public static DeadLetterState ReduceRetryDeadLetterAction(DeadLetterState state, RetryDeadLetterAction action) =>
        new DeadLetterState(isLoading: true, deadLetters: state.DeadLetters, statusFilter: state.StatusFilter);

    [ReducerMethod]
    public static DeadLetterState ReduceDiscardDeadLetterAction(DeadLetterState state, DiscardDeadLetterAction action) =>
        new DeadLetterState(isLoading: true, deadLetters: state.DeadLetters, statusFilter: state.StatusFilter);
}
