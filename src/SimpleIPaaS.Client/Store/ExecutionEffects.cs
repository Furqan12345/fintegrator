using System.Collections.Generic;
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
        var executions = await SafeFetch.GetAsync<FlowExecutionDto[]>(
            _http,
            BuildExecutionsUrl(action.FlowId, action.Status, action.Page, action.PageSize),
            dispatcher,
            "executions");
        if (executions != null)
        {
            dispatcher.Dispatch(new LoadExecutionsResultAction { Executions = executions });
        }
    }

    [EffectMethod]
    public async Task HandleLoadExecutionDetailsAction(LoadExecutionDetailsAction action, IDispatcher dispatcher)
    {
        var steps = await SafeFetch.GetAsync<StepExecutionDto[]>(
            _http, $"api/executions/{action.ExecutionId}/steps", dispatcher, "execution steps");
        if (steps != null)
        {
            dispatcher.Dispatch(new LoadExecutionDetailsResultAction { StepExecutions = steps });
        }
    }

    [EffectMethod]
    public async Task HandleCancelExecutionAction(CancelExecutionAction action, IDispatcher dispatcher)
    {
        await _http.PostAsync($"api/executions/{action.Id}/cancel", null);
        dispatcher.Dispatch(new LoadExecutionsAction
        {
            FlowId = action.FlowId,
            Status = action.Status,
            Page = action.Page,
            PageSize = action.PageSize
        });
    }

    private static string BuildExecutionsUrl(System.Guid? flowId, string status, int page, int pageSize)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (flowId.HasValue)
        {
            query.Add($"flowId={flowId.Value}");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query.Add($"status={status}");
        }

        return $"api/executions?{string.Join("&", query)}";
    }
}
