using System;
using System.Threading.Tasks;
using Fluxor;

namespace SimpleIPaaS.Client.Store;

public class ToastEffects
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);

    [EffectMethod]
    public async Task HandleShowToastAction(ShowToastAction action, IDispatcher dispatcher)
    {
        var toast = new ToastMessage
        {
            Id = Guid.NewGuid(),
            Message = action.Message,
            Level = action.Level
        };

        dispatcher.Dispatch(new ToastAddedAction { Toast = toast });
        await Task.Delay(Lifetime);
        dispatcher.Dispatch(new DismissToastAction { Id = toast.Id });
    }
}
