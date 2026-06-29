using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class FlowEffects
{
    private readonly HttpClient _http;

    public FlowEffects(HttpClient http)
    {
        _http = http;
    }

    [EffectMethod]
    public async Task HandleLoadFlowAction(LoadFlowAction action, IDispatcher dispatcher)
    {
        var flow = await _http.GetFromJsonAsync<IntegrationFlowDto>($"api/integrationflow/{action.Id}");
        if (flow != null)
        {
            dispatcher.Dispatch(new LoadFlowResultAction { Flow = flow });
        }
    }

    [EffectMethod]
    public async Task HandleLoadFlowsAction(LoadFlowsAction action, IDispatcher dispatcher)
    {
        var flows = await _http.GetFromJsonAsync<IntegrationFlowDto[]>("api/integrationflow");
        if (flows != null)
        {
            dispatcher.Dispatch(new LoadFlowsResultAction { Flows = flows });
        }
    }

    [EffectMethod]
    public async Task HandleSaveFlowAction(SaveFlowAction action, IDispatcher dispatcher)
    {
        var response = await _http.PostAsJsonAsync("api/integrationflow", action.Flow);
        var savedFlow = await response.Content.ReadFromJsonAsync<IntegrationFlowDto>();
        if (savedFlow != null)
        {
            dispatcher.Dispatch(new SaveFlowResultAction { Flow = savedFlow });
        }
    }

    [EffectMethod]
    public async Task HandleRunFlowAction(RunFlowAction action, IDispatcher dispatcher)
    {
        var response = await _http.PostAsync($"api/integrationflow/{action.Id}/run", null);
        var resultStr = await response.Content.ReadAsStringAsync();
        dispatcher.Dispatch(new RunFlowResultAction { Result = resultStr });
    }
}
