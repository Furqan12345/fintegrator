using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fluxor;
using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Client.Store;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Tests.Client;

// The second UI audit pass: what the user sees when a request fails.
public sealed class ErrorStateTests
{
    // ---- ErrorText: the Release build trims framework messages to resource keys, so never show ex.Message raw.

    [Fact]
    public void Http_error_with_a_status_names_the_status()
    {
        var ex = new HttpRequestException("net_http_message_not_success_statuscode_reason, 500, Internal Server Error", null, HttpStatusCode.InternalServerError);
        Assert.Equal("The server returned HTTP 500 (Internal Server Error).", ErrorText.Describe(ex));
    }

    [Fact]
    public void Http_error_without_a_status_says_the_api_is_unreachable()
    {
        Assert.Equal("The API could not be reached. Check that it is running, then try again.",
            ErrorText.Describe(new HttpRequestException("net_http_client_execution_error")));
    }

    [Fact]
    public void Json_error_gives_line_and_position()
    {
        var ex = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("{\n  \"a\": ,\n}"));
        Assert.Matches(@"^Invalid JSON at line 2, position \d+\.$", ErrorText.Describe(ex));
    }

    [Fact]
    public void Trimmed_resource_key_is_not_shown()
    {
        Assert.Equal("An unexpected error occurred.", ErrorText.Describe(new InvalidOperationException("InvalidOperation_EnumOpCantHappen")));
        Assert.Equal("Something specific broke.", ErrorText.Describe(new InvalidOperationException("Something specific broke.")));
    }

    // ---- SafeFetch.DescribeFailureAsync: a validation problem's title is generic, the field error is the reason.

    [Fact]
    public async Task Validation_problem_reports_the_field_error_not_the_generic_title()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"title\":\"One or more validation errors occurred.\",\"status\":400,\"errors\":{\"Email\":[\"The Email field is required.\"]}}",
                Encoding.UTF8, "application/problem+json")
        };
        Assert.Equal("The Email field is required.", await SafeFetch.DescribeFailureAsync(response, "fallback"));
    }

    [Fact]
    public async Task Problem_without_field_errors_still_uses_the_title()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"title\":\"A flow with this name already exists.\"}", Encoding.UTF8, "application/problem+json")
        };
        Assert.Equal("A flow with this name already exists.", await SafeFetch.DescribeFailureAsync(response, "fallback"));
    }

    // ---- Load failures stop the spinner, keep cached data and flag the failure (pages render a retry state).

    [Fact]
    public void Failed_connection_load_stops_loading_and_keeps_the_cached_list()
    {
        var cached = new List<ConnectionDto> { new() { Id = Guid.NewGuid(), Name = "Shopify" } };
        var loading = ConnectionReducers.ReduceLoadConnectionsAction(new ConnectionState(false, cached), new LoadConnectionsAction());
        Assert.True(loading.IsLoading);

        var failed = ConnectionReducers.ReduceLoadConnectionsFailedAction(loading, new LoadConnectionsFailedAction());
        Assert.False(failed.IsLoading);
        Assert.True(failed.LoadFailed);
        Assert.Same(cached, failed.Connections);

        var retried = ConnectionReducers.ReduceLoadConnectionsAction(failed, new LoadConnectionsAction());
        Assert.False(retried.LoadFailed);
    }

    [Fact]
    public void Failed_run_list_load_stops_loading()
    {
        var loading = ExecutionReducers.ReduceLoadExecutionsAction(
            new ExecutionState(false, new List<FlowExecutionDto>(), new List<StepExecutionDto>()), new LoadExecutionsAction());
        var failed = ExecutionReducers.ReduceLoadExecutionsFailedAction(loading, new LoadExecutionsFailedAction());
        Assert.False(failed.IsLoading);
        Assert.True(failed.LoadFailed);
    }

    [Fact]
    public void Failed_cross_reference_load_keeps_lists_and_flags_failure()
    {
        var lists = new List<CrossReferenceListDto> { new() { Name = "ShopifyOrders" } };
        var state = new CrossReferenceState(true, lists, string.Empty, new List<CrossReferenceEntryDto>(), 0, 1, 50, string.Empty);
        var failed = CrossReferenceReducers.ReduceLoadCrossReferenceListsFailedAction(state, new LoadCrossReferenceListsFailedAction());
        Assert.False(failed.IsLoading);
        Assert.True(failed.LoadFailed);
        Assert.Same(lists, failed.Lists);
    }

    [Fact]
    public async Task Failed_flow_load_dispatches_a_failure_instead_of_leaving_the_designer_loading()
    {
        var dispatcher = new RecordingDispatcher();
        var effects = new FlowEffects(new HttpClient(new StatusHandler(HttpStatusCode.InternalServerError)) { BaseAddress = new Uri("http://localhost/") });
        var id = Guid.NewGuid();

        await effects.HandleLoadFlowAction(new LoadFlowAction { Id = id }, dispatcher);

        Assert.Contains(dispatcher.Actions, a => a is LoadFlowFailedAction failed && failed.Id == id);
        Assert.DoesNotContain(dispatcher.Actions, a => a is LoadFlowResultAction);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Actions { get; } = new();
        public event EventHandler<ActionDispatchedEventArgs>? ActionDispatched { add { } remove { } }
        public void Dispatch(object action) => Actions.Add(action);
    }
}
