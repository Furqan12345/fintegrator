using System.Collections.Generic;
using System.Linq;
using Fluxor;

namespace SimpleIPaaS.Client.Store;

public static class ToastReducers
{
    private const int MaxVisibleToasts = 4;

    [ReducerMethod]
    public static ToastState ReduceToastAddedAction(ToastState state, ToastAddedAction action)
    {
        var toasts = new List<ToastMessage>(state.Toasts) { action.Toast };
        if (toasts.Count > MaxVisibleToasts)
        {
            toasts.RemoveRange(0, toasts.Count - MaxVisibleToasts);
        }

        return new ToastState(toasts);
    }

    [ReducerMethod]
    public static ToastState ReduceDismissToastAction(ToastState state, DismissToastAction action) =>
        new ToastState(state.Toasts.Where(toast => toast.Id != action.Id).ToList());
}
