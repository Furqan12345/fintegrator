using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class ExecutionEffects
{
    private readonly HttpClient _http;

    public ExecutionEffects(HttpClient http)
    {
        _http = http;
    }

    [EffectMethod]
    public async Task HandleLoadExecutionsAction(LoadExecutionsAction action, IDispatcher dispatcher)
    {
        var executions = await _http.GetFromJsonAsync<FlowExecutionDto[]>("api/executions");
        if (executions != null)
        {
            dispatcher.Dispatch(new LoadExecutionsResultAction { Executions = executions });
        }
    }

    [EffectMethod]
    public async Task HandleLoadExecutionDetailsAction(LoadExecutionDetailsAction action, IDispatcher dispatcher)
    {
        var steps = await _http.GetFromJsonAsync<StepExecutionDto[]>($"api/executions/{action.ExecutionId}/steps");
        if (steps != null)
        {
            dispatcher.Dispatch(new LoadExecutionDetailsResultAction { StepExecutions = steps });
        }
    }
}
