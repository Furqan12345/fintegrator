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
            BuildExecutionsUrl(action),
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
            IntegrationId = action.IntegrationId,
            Status = action.Status,
            Search = action.Search,
            SinceUtc = action.SinceUtc,
            UntilUtc = action.UntilUtc,
            Page = action.Page,
            PageSize = action.PageSize,
            IncludeTests = action.IncludeTests
        });
    }

    private static string BuildExecutionsUrl(LoadExecutionsAction action)
    {
        var query = new List<string> { $"page={action.Page}", $"pageSize={action.PageSize}" };
        if (action.FlowId.HasValue)
        {
            query.Add($"flowId={action.FlowId.Value}");
        }

        if (action.IntegrationId.HasValue)
        {
            query.Add($"integrationId={action.IntegrationId.Value}");
        }

        if (!string.IsNullOrWhiteSpace(action.Status))
        {
            query.Add($"status={System.Uri.EscapeDataString(action.Status)}");
        }

        if (!string.IsNullOrWhiteSpace(action.Search))
        {
            query.Add($"search={System.Uri.EscapeDataString(action.Search.Trim())}");
        }

        if (action.SinceUtc.HasValue)
        {
            query.Add($"sinceUtc={System.Uri.EscapeDataString(action.SinceUtc.Value.ToUniversalTime().ToString("o"))}");
        }

        if (action.UntilUtc.HasValue)
        {
            query.Add($"untilUtc={System.Uri.EscapeDataString(action.UntilUtc.Value.ToUniversalTime().ToString("o"))}");
        }

        if (action.IncludeTests)
        {
            query.Add("includeTests=true");
        }

        return $"api/executions?{string.Join("&", query)}";
    }
}
