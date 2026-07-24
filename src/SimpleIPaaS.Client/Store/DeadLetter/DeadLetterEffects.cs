using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class DeadLetterEffects
{
    private readonly HttpClient _http;

    public DeadLetterEffects(HttpClient http)
    {
        _http = http;
    }

    [EffectMethod]
    public async Task HandleLoadDeadLettersAction(LoadDeadLettersAction action, IDispatcher dispatcher)
    {
        var url = string.IsNullOrWhiteSpace(action.Status)
            ? "api/deadletters"
            : $"api/deadletters?status={action.Status}";
        var deadLetters = await _http.GetFromJsonAsync<DeadLetterDto[]>(url);
        if (deadLetters != null)
        {
            dispatcher.Dispatch(new LoadDeadLettersResultAction { DeadLetters = deadLetters });
        }
    }

    [EffectMethod]
    public async Task HandleRetryDeadLetterAction(RetryDeadLetterAction action, IDispatcher dispatcher)
    {
        await _http.PostAsync($"api/deadletters/{action.Id}/retry", null);
        dispatcher.Dispatch(new LoadDeadLettersAction { Status = action.StatusFilter });
    }

    [EffectMethod]
    public async Task HandleDiscardDeadLetterAction(DiscardDeadLetterAction action, IDispatcher dispatcher)
    {
        await _http.PostAsync($"api/deadletters/{action.Id}/discard", null);
        dispatcher.Dispatch(new LoadDeadLettersAction { Status = action.StatusFilter });
    }
}
