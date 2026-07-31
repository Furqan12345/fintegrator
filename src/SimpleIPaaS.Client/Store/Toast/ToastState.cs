using System.Collections.Generic;
using Fluxor;

namespace SimpleIPaaS.Client.Store;

[FeatureState]
public class ToastState
{
    public IReadOnlyList<ToastMessage> Toasts { get; }

    private ToastState()
    {
        Toasts = new List<ToastMessage>();
    }

    public ToastState(IReadOnlyList<ToastMessage> toasts)
    {
        Toasts = toasts;
    }
}
