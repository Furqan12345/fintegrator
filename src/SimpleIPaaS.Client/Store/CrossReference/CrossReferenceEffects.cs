using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class CrossReferenceEffects
{
    private readonly HttpClient _http;

    public CrossReferenceEffects(HttpClient http)
    {
        _http = http;
    }

    [EffectMethod]
    public async Task HandleLoadCrossReferenceListsAction(LoadCrossReferenceListsAction action, IDispatcher dispatcher)
    {
        var lists = await SafeFetch.GetAsync<CrossReferenceListDto[]>(_http, "api/crossreferences", dispatcher, "cross-reference lists");
        dispatcher.Dispatch(new LoadCrossReferenceListsResultAction { Lists = lists ?? Array.Empty<CrossReferenceListDto>() });
    }

    [EffectMethod]
    public async Task HandleLoadCrossReferenceEntriesAction(LoadCrossReferenceEntriesAction action, IDispatcher dispatcher)
    {
        var url = $"api/crossreferences/{Uri.EscapeDataString(action.ListName)}/entries?page={action.Page}&pageSize=50";
        if (!string.IsNullOrWhiteSpace(action.Search))
        {
            url += $"&search={Uri.EscapeDataString(action.Search)}";
        }

        var result = await SafeFetch.GetAsync<CrossReferenceEntryPageDto>(_http, url, dispatcher, $"entries for '{action.ListName}'");
        dispatcher.Dispatch(new LoadCrossReferenceEntriesResultAction
        {
            ListName = action.ListName,
            Result = result ?? new CrossReferenceEntryPageDto { Page = action.Page }
        });
    }

    [EffectMethod]
    public async Task HandleCreateCrossReferenceListAction(CreateCrossReferenceListAction action, IDispatcher dispatcher)
    {
        var payload = new CrossReferenceListDto { Name = action.Name, Description = action.Description };
        var created = await SafeFetch.SendAsync<CrossReferenceListDto>(
            () => _http.PostAsJsonAsync("api/crossreferences", payload),
            dispatcher,
            $"cross-reference list '{action.Name}'");

        if (created != null)
        {
            dispatcher.Dispatch(new ShowToastAction { Message = $"List '{created.Name}' created." });
        }

        dispatcher.Dispatch(new LoadCrossReferenceListsAction());
    }

    [EffectMethod]
    public async Task HandleDeleteCrossReferenceListAction(DeleteCrossReferenceListAction action, IDispatcher dispatcher)
    {
        var result = await SafeFetch.SendAsync<CrossReferenceCommandResultDto>(
            () => _http.DeleteAsync($"api/crossreferences/{Uri.EscapeDataString(action.Name)}"),
            dispatcher,
            $"deletion of list '{action.Name}'");

        if (result != null)
        {
            dispatcher.Dispatch(new ShowToastAction { Message = $"List '{action.Name}' deleted ({result.Removed} entries removed)." });
            dispatcher.Dispatch(new CloseCrossReferenceListAction());
        }

        dispatcher.Dispatch(new LoadCrossReferenceListsAction());
    }

    [EffectMethod]
    public async Task HandleClearCrossReferenceListAction(ClearCrossReferenceListAction action, IDispatcher dispatcher)
    {
        var result = await SafeFetch.SendAsync<CrossReferenceCommandResultDto>(
            () => _http.DeleteAsync($"api/crossreferences/{Uri.EscapeDataString(action.Name)}/entries"),
            dispatcher,
            $"clearing of list '{action.Name}'");

        if (result != null)
        {
            dispatcher.Dispatch(new ShowToastAction { Message = $"Cleared {result.Removed} entries from '{action.Name}'." });
            dispatcher.Dispatch(new LoadCrossReferenceEntriesAction { ListName = action.Name, Page = 1 });
        }

        dispatcher.Dispatch(new LoadCrossReferenceListsAction());
    }

    [EffectMethod]
    public async Task HandleDeleteCrossReferenceEntryAction(DeleteCrossReferenceEntryAction action, IDispatcher dispatcher)
    {
        var result = await SafeFetch.SendAsync<CrossReferenceCommandResultDto>(
            () => _http.DeleteAsync($"api/crossreferences/entries/{action.Id}"),
            dispatcher,
            "deletion of the cross-reference entry");

        if (result != null)
        {
            dispatcher.Dispatch(new ShowToastAction { Message = "Entry deleted." });
        }

        dispatcher.Dispatch(new LoadCrossReferenceEntriesAction
        {
            ListName = action.ListName,
            Search = action.Search,
            Page = action.Page
        });
        dispatcher.Dispatch(new LoadCrossReferenceListsAction());
    }
}
