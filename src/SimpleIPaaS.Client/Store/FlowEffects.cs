using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
        var flow = await SafeFetch.GetAsync<IntegrationFlowDto>(_http, $"api/integrationflow/{action.Id}", dispatcher, "the flow");
        if (flow != null)
        {
            dispatcher.Dispatch(new LoadFlowResultAction { Flow = flow });
        }
    }

    [EffectMethod]
    public async Task HandleLoadFlowsAction(LoadFlowsAction action, IDispatcher dispatcher)
    {
        var flows = await SafeFetch.GetAsync<IntegrationFlowDto[]>(_http, "api/integrationflow", dispatcher, "flows");
        if (flows != null)
        {
            dispatcher.Dispatch(new LoadFlowsResultAction { Flows = flows });
        }
    }

    [EffectMethod]
    public async Task HandleSaveFlowAction(SaveFlowAction action, IDispatcher dispatcher)
    {
        IntegrationFlowDto savedFlow;
        try
        {
            var response = action.Flow.Id == Guid.Empty
                ? await _http.PostAsJsonAsync("api/integrationflow", action.Flow)
                : await _http.PutAsJsonAsync($"api/integrationflow/{action.Flow.Id}", action.Flow);

            if (!response.IsSuccessStatusCode)
            {
                dispatcher.Dispatch(new RunFlowResultAction { Result = await response.Content.ReadAsStringAsync() });
                dispatcher.Dispatch(new ShowToastAction { Message = "Flow could not be saved.", Level = "error" });
                return;
            }

            savedFlow = await response.Content.ReadFromJsonAsync<IntegrationFlowDto>() ?? action.Flow;
        }
        catch (Exception)
        {
            dispatcher.Dispatch(new ShowToastAction { Message = "Flow could not be saved. The API may be unavailable.", Level = "error" });
            return;
        }


        if (savedFlow != null)
        {
            dispatcher.Dispatch(new SaveFlowResultAction { Flow = savedFlow });
            dispatcher.Dispatch(new ShowToastAction { Message = $"Flow \"{savedFlow.Name}\" saved." });
            if (action.RunAfterSave && savedFlow.Id != Guid.Empty)
            {
                dispatcher.Dispatch(new RunFlowAction { Id = savedFlow.Id });
            }
        }
    }

    [EffectMethod]
    public async Task HandleRunFlowAction(RunFlowAction action, IDispatcher dispatcher)
    {
        HttpResponseMessage response;
        string resultStr;
        try
        {
            response = await _http.PostAsync($"api/integrationflow/{action.Id}/run", null);
            resultStr = await response.Content.ReadAsStringAsync();
        }
        catch (Exception)
        {
            dispatcher.Dispatch(new ShowToastAction { Message = "Flow run could not be started. The API may be unavailable.", Level = "error" });
            return;
        }

        Guid? executionId = null;

        try
        {
            using var document = JsonDocument.Parse(resultStr);
            if (document.RootElement.TryGetProperty("executionId", out var executionIdElement) &&
                executionIdElement.ValueKind == JsonValueKind.String &&
                Guid.TryParse(executionIdElement.GetString(), out var parsedExecutionId))
            {
                executionId = parsedExecutionId;
            }
        }
        catch (JsonException)
        {
        }

        dispatcher.Dispatch(new RunFlowResultAction { Result = resultStr, ExecutionId = executionId });
        dispatcher.Dispatch(response.IsSuccessStatusCode
            ? new ShowToastAction { Message = "Flow run started." }
            : new ShowToastAction { Message = "Flow run could not be started.", Level = "error" });
    }
}
