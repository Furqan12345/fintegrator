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
        var connections = await _http.GetFromJsonAsync<ConnectionDto[]>("api/connections");
        if (connections != null)
        {
            dispatcher.Dispatch(new LoadConnectionsResultAction { Connections = connections });
        }
    }

    [EffectMethod]
    public async Task HandleSaveConnectionAction(SaveConnectionAction action, IDispatcher dispatcher)
    {
        var response = await _http.PostAsJsonAsync("api/connections", action.Connection);
        var savedConnection = await response.Content.ReadFromJsonAsync<ConnectionDto>();
        if (savedConnection != null)
        {
            dispatcher.Dispatch(new SaveConnectionResultAction { Connection = savedConnection });
        }
    }
}
