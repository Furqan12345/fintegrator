using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class ConnectionEffects
{
    private readonly HttpClient _http;

    public ConnectionEffects(HttpClient http)
    {
        _http = http;
    }

    [EffectMethod]
    public async Task HandleLoadConnectionsAction(LoadConnectionsAction action, IDispatcher dispatcher)
    {
        var connections = await SafeFetch.GetAsync<ConnectionDto[]>(_http, "api/connections", dispatcher, "connections");
        if (connections != null)
        {
            dispatcher.Dispatch(new LoadConnectionsResultAction { Connections = connections });
        }
    }

    [EffectMethod]
    public async Task HandleSaveConnectionAction(SaveConnectionAction action, IDispatcher dispatcher)
    {
        var isNew = action.Connection.Id == Guid.Empty;
        var savedConnection = await SafeFetch.SendAsync<ConnectionDto>(
            () => isNew
                ? _http.PostAsJsonAsync("api/connections", action.Connection)
                : _http.PutAsJsonAsync($"api/connections/{action.Connection.Id}", action.Connection),
            dispatcher,
            "the connection");

        if (savedConnection == null)
        {
            dispatcher.Dispatch(new SaveConnectionFailedAction());
            return;
        }

        dispatcher.Dispatch(new SaveConnectionResultAction { Connection = savedConnection });
        dispatcher.Dispatch(new ShowToastAction { Message = isNew ? "Connection created." : "Connection saved." });
    }
}
