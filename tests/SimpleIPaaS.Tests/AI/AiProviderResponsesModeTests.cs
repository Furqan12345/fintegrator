using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

// The Responses API mode: request shape (flattened tools, instructions, reasoning, store:false),
// the multi-function_call loop with call_id round-trips, output_text + url_citation extraction,
// incomplete handling, and the Chat Completions back-compat path for rows stored before the mode
// existed. Every test stubs the HTTP layer — no live model call is made.
public sealed class AiProviderResponsesModeTests
{
    private static readonly AiProviderRequestOptions ResponsesWithWebSearch = new()
    {
        ApiMode = AiApiModes.Responses,
        ReasoningEffort = AiReasoningEfforts.Low,
        EnableWebSearch = true
    };

    [Fact]
    public async Task BuildAsync_ResponsesMode_SendsFlattenedToolsInstructionsReasoningAndStoreFalse()
    {
        var draft = DraftJson("Responses shape draft");
        var handler = StubHttpMessageHandler.Json(ResponsesMessage(draft));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = CreateService(clientFactory);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://127.0.0.1:9310/v1",
            "gpt-5.6-test",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fetch Shopify orders." },
            CancellationToken.None,
            providerOptions: ResponsesWithWebSearch);

        Assert.Equal("Responses shape draft", result.Flow.Name);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:9310/v1/responses", request.RequestUri!.ToString());

        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = body.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(32768, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Contains("You build SimpleIPaaS integration-flow drafts", root.GetProperty("instructions").GetString());

        // System content is instructions, never an input item; the user turn is an input_text part.
        var input = root.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());

        // Tools are flattened ({type,name,description,parameters}) and web search rides along.
        var tools = root.GetProperty("tools").EnumerateArray().ToList();
        var firstTool = tools[0];
        Assert.Equal("function", firstTool.GetProperty("type").GetString());
        Assert.True(firstTool.TryGetProperty("name", out _));
        Assert.False(firstTool.TryGetProperty("function", out _));
        Assert.Equal("object", firstTool.GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("web_search", tools[^1].GetProperty("type").GetString());
        Assert.False(tools[^1].TryGetProperty("name", out _));
    }

    [Fact]
    public async Task BuildAsync_ResponsesMode_ExecutesEveryFunctionCallAndRoundTripsCallIds()
    {
        var draft = DraftJson("Two-call draft");
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = body.Contains("\"function_call_output\"", StringComparison.Ordinal)
                ? ResponsesMessage(draft)
                : JsonSerializer.Serialize(new
                {
                    status = "completed",
                    output = new object[]
                    {
                        new { type = "function_call", call_id = "call_alpha", name = "unknown_research_tool", arguments = "{}" },
                        new { type = "function_call", call_id = "call_beta", name = "unknown_research_tool", arguments = "{\"query\":\"shopify orders\"}" }
                    }
                });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = CreateService(clientFactory);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://127.0.0.1:9310/v1",
            "gpt-5.6-test",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fetch Shopify orders." },
            CancellationToken.None,
            providerOptions: new AiProviderRequestOptions { ApiMode = AiApiModes.Responses });

        Assert.Equal("Two-call draft", result.Flow.Name);
        Assert.Equal(2, handler.RequestBodies.Count);

        using var followUp = JsonDocument.Parse(handler.RequestBodies[1]);
        var items = followUp.RootElement.GetProperty("input").EnumerateArray().ToList();
        static bool IsItemType(JsonElement item, string type) =>
            item.TryGetProperty("type", out var value) && value.GetString() == type;
        // Both calls ran, and both outputs are keyed by the provider's call_id.
        var outputs = items.Where(item => IsItemType(item, "function_call_output")).ToList();
        Assert.Equal(2, outputs.Count);
        Assert.Contains("call_alpha", outputs.Select(item => item.GetProperty("call_id").GetString()));
        Assert.Contains("call_beta", outputs.Select(item => item.GetProperty("call_id").GetString()));
        Assert.Contains("call_alpha", items.Where(item => IsItemType(item, "function_call"))
            .Select(item => item.GetProperty("call_id").GetString()));
        // store:false means nothing is kept server-side, so the whole transcript is re-sent,
        // including the original user turn and the assistant's own function calls.
        Assert.Contains(items, item => item.TryGetProperty("role", out var role) && role.GetString() == "user");
    }

    [Fact]
    public async Task BuildAsync_ResponsesMode_ConcatenatesOutputTextAndCapturesCitationSources()
    {
        var response = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new object[]
            {
                new
                {
                    type = "message",
                    content = new object[]
                    {
                        new { type = "output_text", text = "Your draft fetches orders ", annotations = Array.Empty<object>() },
                        new
                        {
                            type = "output_text",
                            text = "and stores them in the cross-reference list.",
                            annotations = new object[]
                            {
                                new { type = "url_citation", url = "https://shopify.dev/docs/api/orders", title = "Shopify Orders" }
                            }
                        }
                    }
                }
            }
        });
        using var clientFactory = new StubHttpClientFactory(StubHttpMessageHandler.Json(response));
        var service = CreateService(clientFactory);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://127.0.0.1:9310/v1",
            "gpt-5.6-test",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "review my draft" },
            CancellationToken.None,
            allowProseReply: true,
            providerOptions: new AiProviderRequestOptions { ApiMode = AiApiModes.Responses, EnableWebSearch = true });

        Assert.True(result.IsConversational);
        Assert.Equal("Your draft fetches orders and stores them in the cross-reference list.", result.Summary);
        Assert.Contains("Web search — Shopify Orders (https://shopify.dev/docs/api/orders)", result.Sources);
    }

    [Theory]
    [InlineData("max_output_tokens", "output tokens")]
    [InlineData("content_filter", "content-policy")]
    public async Task BuildAsync_ResponsesMode_ReportsIncompleteResponsesInsteadOfDrafting(string reason, string expected)
    {
        var response = JsonSerializer.Serialize(new
        {
            status = "incomplete",
            incomplete_details = new { reason },
            output = new object[]
            {
                new { type = "message", content = new object[] { new { type = "output_text", text = "half a dr" } } }
            }
        });
        using var clientFactory = new StubHttpClientFactory(StubHttpMessageHandler.Json(response));
        var service = CreateService(clientFactory);

        var exception = await Assert.ThrowsAsync<AiFlowBuilderException>(() => service.BuildAsync(
            "OpenAICompatible",
            "http://127.0.0.1:9310/v1",
            "gpt-5.6-test",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fetch Shopify orders." },
            CancellationToken.None,
            providerOptions: new AiProviderRequestOptions { ApiMode = AiApiModes.Responses }));

        Assert.Contains(expected, exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_ResponsesMode_MapsResponsesErrorShape()
    {
        var handler = StubHttpMessageHandler.Json(
            "{\"error\":{\"message\":\"Unknown parameter: 'store' is not supported.\",\"type\":\"invalid_request_error\",\"code\":\"unsupported_parameter\"}}",
            HttpStatusCode.BadRequest);
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = CreateService(clientFactory);

        var exception = await Assert.ThrowsAsync<ProviderRequestException>(() => service.TestConnectionAsync(
            "OpenAICompatible",
            "http://127.0.0.1:9310/v1",
            "gpt-5.6-test",
            "provider-test-key",
            CancellationToken.None,
            new AiProviderRequestOptions { ApiMode = AiApiModes.Responses }));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("Unknown parameter", exception.ProviderMessage);
        var described = ProviderErrorMessages.Describe(exception);
        Assert.Contains("400", described);
        Assert.Contains("Unknown parameter", described);
    }

    [Fact]
    public async Task TestConnectionAsync_ResponsesMode_SurfacesProviderMessageVerbatimForAzureApiVersionErrors()
    {
        // Responses on Azure needs an api-version that supports it; the provider's own words are
        // what the operator must see, with the numeric status attached.
        var handler = StubHttpMessageHandler.Json(
            "{\"error\":{\"message\":\"The API version 2023-05-15 does not support the Responses API.\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_version\"}}",
            (HttpStatusCode)404);
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = CreateService(clientFactory);

        var exception = await Assert.ThrowsAsync<ProviderRequestException>(() => service.TestConnectionAsync(
            "AzureOpenAI",
            "http://127.0.0.1:9310/openai/deployments/orders?api-version=2023-05-15",
            "orders",
            "provider-test-key",
            CancellationToken.None,
            new AiProviderRequestOptions { ApiMode = AiApiModes.Responses }));

        Assert.Equal("http://127.0.0.1:9310/openai/deployments/orders/responses?api-version=2023-05-15",
            Assert.Single(handler.Requests).RequestUri!.ToString());
        var described = ProviderErrorMessages.Describe(exception);
        Assert.Contains("404", described);
        Assert.Contains("does not support the Responses API", described);
    }

    [Fact]
    public async Task BuildAsync_LegacyStoredChatCompletionsEndpointStillWorksUntouched()
    {
        // A row saved before the API mode existed: the value still carries /chat/completions and
        // no mode. It keeps working exactly as before — same URL, same body shape, no web search.
        var draft = DraftJson("Legacy draft");
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = draft } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = CreateService(clientFactory);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fetch Shopify orders." },
            CancellationToken.None);
        Assert.Equal("Legacy draft", result.Flow.Name);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost/v1/chat/completions", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(body.RootElement.TryGetProperty("messages", out var messages));
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.False(body.RootElement.TryGetProperty("store", out _));
        Assert.False(body.RootElement.TryGetProperty("input", out _));
        Assert.True(body.RootElement.GetProperty("tools")[0].TryGetProperty("function", out _));
        Assert.DoesNotContain("\"web_search\"", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_WebSearchIsIgnoredOutsideResponsesMode()
    {
        var draft = DraftJson("Chat draft");
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = draft } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = CreateService(clientFactory);

        await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost:11434/v1",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fetch Shopify orders." },
            CancellationToken.None,
            providerOptions: new AiProviderRequestOptions
            {
                ApiMode = AiApiModes.ChatCompletions,
                ReasoningEffort = AiReasoningEfforts.Low,
                EnableWebSearch = true
            });

        Assert.DoesNotContain("\"web_search\"", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning\"", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Equal("http://localhost:11434/v1/chat/completions", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    private static string ResponsesMessage(string text) => JsonSerializer.Serialize(new
    {
        status = "completed",
        usage = new { input_tokens = 120, output_tokens = 40 },
        output = new object[]
        {
            new { type = "message", content = new object[] { new { type = "output_text", text, annotations = Array.Empty<object>() } } }
        }
    });

    private static string DraftJson(string name) => JsonSerializer.Serialize(new
    {
        flow = new
        {
            name,
            nodes = new[]
            {
                new IntegrationStepDto { Id = Guid.NewGuid(), NodeName = "Fetch orders", StepType = "HttpAction", EndpointUrl = "https://api.example.test/orders" }
            },
            edges = Array.Empty<IntegrationEdgeDto>()
        },
        summary = "Valid draft.",
        warnings = Array.Empty<string>(),
        sources = Array.Empty<string>()
    });

    private static AiFlowBuilderService CreateService(IHttpClientFactory clientFactory) =>
        new(clientFactory,
            new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(),
                new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);
}
