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
        var deadLetters = await SafeFetch.GetAsync<DeadLetterDto[]>(_http, url, dispatcher, "dead letters");
        if (deadLetters != null)
        {
            dispatcher.Dispatch(new LoadDeadLettersResultAction { DeadLetters = deadLetters });
        }
    }

    [EffectMethod]
    public async Task HandleRetryDeadLetterAction(RetryDeadLetterAction action, IDispatcher dispatcher)
    {
        await PostAndReloadAsync($"api/deadletters/{action.Id}/retry", "Dead letter queued for retry.", "Retry failed.", action.StatusFilter, dispatcher);
    }

    [EffectMethod]
    public async Task HandleDiscardDeadLetterAction(DiscardDeadLetterAction action, IDispatcher dispatcher)
    {
        await PostAndReloadAsync($"api/deadletters/{action.Id}/discard", "Dead letter discarded.", "Discard failed.", action.StatusFilter, dispatcher);
    }

    private async Task PostAndReloadAsync(string url, string successMessage, string failureMessage, string statusFilter, IDispatcher dispatcher)
    {
        try
        {
            var response = await _http.PostAsync(url, null);
            dispatcher.Dispatch(response.IsSuccessStatusCode
                ? new ShowToastAction { Message = successMessage }
                : new ShowToastAction { Message = await SafeFetch.DescribeFailureAsync(response, failureMessage), Level = "error" });
        }
        catch (Exception)
        {
            dispatcher.Dispatch(new ShowToastAction { Message = $"{failureMessage} The API may be unavailable.", Level = "error" });
        }

        dispatcher.Dispatch(new LoadDeadLettersAction { Status = statusFilter });
    }
}
