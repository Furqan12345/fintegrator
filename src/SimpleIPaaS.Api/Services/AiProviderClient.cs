using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Services;

// Everything a provider row contributes to a request besides type/endpoint/model/key.
public sealed class AiProviderRequestOptions
{
    public static AiProviderRequestOptions Default { get; } = new();

    public string ApiMode { get; init; } = AiApiModes.ChatCompletions;
    public string? ReasoningEffort { get; init; }
    public bool EnableWebSearch { get; init; }

    public static AiProviderRequestOptions From(AiProviderSettings provider) => new()
    {
        ApiMode = AiApiModes.Normalize(provider.ApiMode),
        ReasoningEffort = AiReasoningEfforts.Normalize(provider.ReasoningEffort),
        EnableWebSearch = provider.EnableWebSearch
    };
}

// One tool call, in the shape both loops already understand: id = the chat tool_call id or
// the Responses call_id (that is the key the result must be sent back under).
internal sealed record ProviderToolCall(string Id, string Name, string Arguments);

// A provider reply normalized for the agent loop: the assistant turn as a chat-shaped
// message (so the loop can append it to the transcript unchanged and the mode strategy
// translates it back on the next request), the assistant text, and the metadata only the
// Responses API returns (citations, incomplete reason, usage).
internal sealed class AiProviderTurn
{
    public static AiProviderTurn Empty { get; } = new();

    public bool HasMessage { get; init; }
    public string MessageJson { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public IReadOnlyList<ProviderToolCall> ToolCalls { get; init; } = Array.Empty<ProviderToolCall>();
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    public string? IncompleteReason { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

internal sealed record AiProviderRequestContext(
    string Model,
    bool IncludeModel,
    JsonArray Messages,
    int OutputTokens,
    bool IncludeTools,
    bool EnableWebSearch,
    string? ReasoningEffort,
    bool IsFlowBuild,
    bool ExpectJsonDraft);

// Strategy per API mode: one request builder and one response parser. The transport
// (endpoint derivation, retries, auth, error mapping) is shared and lives in
// AiProviderClient, so the agent loop in AiFlowBuilderService is written once.
internal interface IAiProviderApiStrategy
{
    string ApiMode { get; }

    // Only the chat-completions shape has the legacy max_tokens ↔ max_completion_tokens alias dance.
    bool UsesCompletionTokenLimitFallback { get; }

    string ResponseLabel { get; }

    JsonObject BuildRequest(AiProviderRequestContext context);

    AiProviderTurn ParseResponse(JsonDocument response);
}

internal static class AiProviderApiStrategy
{
    public static IAiProviderApiStrategy For(string? apiMode) =>
        AiApiModes.IsResponses(apiMode) ? ResponsesStrategy : ChatCompletionsStrategy;

    private static readonly ChatCompletionsApiStrategy ChatCompletionsStrategy = new();
    private static readonly ResponsesApiStrategy ResponsesStrategy = new();
}

// The draft envelope is intentionally permissive inside flow: step configs are a dynamic
// per-card JSON string and the builder validates the graph separately. The schema still
// guarantees a JSON object with the expected envelope instead of prose or broken JSON.
internal static class AiFlowDraftSchema
{
    public static JsonObject ChatResponseFormat() => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject
        {
            ["name"] = "flow_draft",
            ["strict"] = false,
            ["schema"] = Create()
        }
    };

    public static JsonObject ResponsesFormat() => new()
    {
        ["type"] = "json_schema",
        ["name"] = "flow_draft",
        ["strict"] = false,
        ["schema"] = Create()
    };

    private static JsonObject Create() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("flow", "summary", "warnings", "sources"),
        ["properties"] = new JsonObject
        {
            ["flow"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = true,
                ["required"] = new JsonArray("name", "nodes", "edges"),
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["description"] = new JsonObject { ["type"] = "string" },
                    ["triggerType"] = new JsonObject { ["type"] = "string" },
                    ["cronExpression"] = new JsonObject { ["type"] = "string" },
                    ["nodes"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = true,
                            ["required"] = new JsonArray("id", "nodeName", "stepType"),
                            ["properties"] = new JsonObject
                            {
                                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Unique GUID; preserve existing node ids." },
                                ["nodeName"] = new JsonObject { ["type"] = "string" },
                                ["stepType"] = new JsonObject { ["type"] = "string" },
                                ["positionX"] = new JsonObject { ["type"] = "number" },
                                ["positionY"] = new JsonObject { ["type"] = "number" },
                                ["endpointUrl"] = new JsonObject { ["type"] = "string" },
                                ["httpMethod"] = new JsonObject { ["type"] = "string" },
                                ["urlMode"] = new JsonObject { ["type"] = "string", ["description"] = "Manual or Connection." },
                                ["authType"] = new JsonObject { ["type"] = "string" },
                                ["connectionId"] = new JsonObject { ["type"] = "string", ["description"] = "GUID when urlMode is Connection." },
                                ["mappingCode"] = new JsonObject { ["type"] = "string" },
                                ["urlCode"] = new JsonObject { ["type"] = "string" },
                                ["preFlightCode"] = new JsonObject { ["type"] = "string" },
                                ["postFlightCode"] = new JsonObject { ["type"] = "string" },
                                ["stepConfig"] = new JsonObject { ["type"] = "string", ["description"] = "A JSON object serialized as a string, never a nested object." }
                            }
                        }
                    },
                    ["edges"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = true,
                            ["required"] = new JsonArray("sourceNodeId", "targetNodeId", "sourcePortId", "targetPortId"),
                            ["properties"] = new JsonObject
                            {
                                ["id"] = new JsonObject { ["type"] = "string" },
                                ["sourceNodeId"] = new JsonObject { ["type"] = "string" },
                                ["targetNodeId"] = new JsonObject { ["type"] = "string" },
                                ["sourcePortId"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "right for normal nodes and Schedule/FanOut; true or false for Branch; right or completed for ForEach."
                                },
                                ["targetPortId"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "left for every node; Schedule cannot be an edge target."
                                },
                                ["condition"] = new JsonObject { ["type"] = "string" },
                                ["order"] = new JsonObject { ["type"] = "integer" }
                            }
                        }
                    }
                }
            },
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["warnings"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" }
            },
            ["sources"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" }
            }
        }
    };
}

// POST {root}/chat/completions — the original shape, unchanged, so every self-hosted
// OpenAI-compatible server and every existing provider row keeps working byte for byte.
internal sealed class ChatCompletionsApiStrategy : IAiProviderApiStrategy
{
    public string ApiMode => AiApiModes.ChatCompletions;
    public bool UsesCompletionTokenLimitFallback => true;
    public string ResponseLabel => "chat-completions";

    public JsonObject BuildRequest(AiProviderRequestContext context)
    {
        var body = new JsonObject { ["messages"] = context.Messages.DeepClone() };
        if (UsesCompletionTokenLimit(context.Model)) body["max_completion_tokens"] = context.OutputTokens;
        else body["max_tokens"] = context.OutputTokens;
        if (context.IncludeModel) body["model"] = context.Model;
        if (context.IncludeTools)
        {
            body["tools"] = AiFlowAgentTools.CreateToolSchemas();
            body["tool_choice"] = "auto";
        }
        if (context.ExpectJsonDraft && !context.IncludeTools)
            body["response_format"] = AiFlowDraftSchema.ChatResponseFormat();
        if (context.IsFlowBuild && IsGpt56Model(context.Model)) body["reasoning_effort"] = "none";
        return body;
    }

    public AiProviderTurn ParseResponse(JsonDocument response)
    {
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return AiProviderTurn.Empty;

        return new AiProviderTurn
        {
            HasMessage = true,
            MessageJson = message.GetRawText(),
            Content = ReadMessageContent(message),
            ToolCalls = ReadChatToolCalls(message),
            InputTokens = ProviderJson.ReadInt(root, "usage", "prompt_tokens"),
            OutputTokens = ProviderJson.ReadInt(root, "usage", "completion_tokens")
        };
    }

    // Content is a plain string for every text reply; a provider that returns parts keeps the
    // previous behaviour (the raw JSON is passed through, never silently emptied).
    private static string ReadMessageContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => content.GetRawText()
        };
    }

    private static bool UsesCompletionTokenLimit(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpt56Model(string model) => model.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase);

    private static List<ProviderToolCall> ReadChatToolCalls(JsonElement message)
    {
        var calls = new List<ProviderToolCall>();
        if (!message.TryGetProperty("tool_calls", out var elements) || elements.ValueKind != JsonValueKind.Array) return calls;
        foreach (var item in elements.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !item.TryGetProperty("function", out var function) ||
                !function.TryGetProperty("name", out var name) || !function.TryGetProperty("arguments", out var args)) continue;
            var argsJson = args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : args.GetRawText();
            calls.Add(new ProviderToolCall(id.GetString() ?? string.Empty, name.GetString() ?? string.Empty, argsJson));
        }
        return calls;
    }
}

// POST {root}/responses (OpenAI Responses API), non-streaming and store:false. Because
// nothing is stored server-side, every turn re-sends the whole transcript — the chat-shaped
// messages the agent loop keeps are translated here into Responses input items, and the
// assistant turn is translated back so the loop itself stays mode-agnostic.
internal sealed class ResponsesApiStrategy : IAiProviderApiStrategy
{
    public string ApiMode => AiApiModes.Responses;
    public bool UsesCompletionTokenLimitFallback => false;
    public string ResponseLabel => "responses";

    public JsonObject BuildRequest(AiProviderRequestContext context)
    {
        var input = BuildInput(context.Messages, out var instructions);
        var body = new JsonObject
        {
            ["store"] = false,
            ["input"] = input,
            ["max_output_tokens"] = context.OutputTokens
        };
        if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;
        if (context.IncludeModel) body["model"] = context.Model;
        if (context.IncludeTools)
        {
            body["tools"] = BuildTools(context.EnableWebSearch);
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }
        if (context.ExpectJsonDraft && !context.IncludeTools)
            body["text"] = new JsonObject { ["format"] = AiFlowDraftSchema.ResponsesFormat() };
        if (!string.IsNullOrWhiteSpace(context.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = context.ReasoningEffort };
        return body;
    }

    public AiProviderTurn ParseResponse(JsonDocument response)
    {
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return AiProviderTurn.Empty;

        var text = new StringBuilder();
        var sources = new List<string>();
        var calls = new List<ProviderToolCall>();
        var hasMessage = false;

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = ProviderJson.ReadString(item, "type");
                if (string.Equals(type, "message", StringComparison.Ordinal))
                {
                    hasMessage = true;
                    AppendMessageText(item, text, sources);
                }
                else if (string.Equals(type, "function_call", StringComparison.Ordinal))
                {
                    // A turn made of function calls is a valid assistant message (chat
                    // completions sends the same turn as message.content:null + tool_calls).
                    hasMessage = true;
                    var callId = ProviderJson.ReadString(item, "call_id");
                    if (callId.Length == 0) callId = ProviderJson.ReadString(item, "id");
                    calls.Add(new ProviderToolCall(callId, ProviderJson.ReadString(item, "name"), ProviderJson.ReadString(item, "arguments")));
                }
            }
        }

        var incompleteReason = IsIncomplete(root) ? ProviderJson.ReadString(root, "incomplete_details", "reason") : null;
        if (incompleteReason is not null && incompleteReason.Length == 0) incompleteReason = "unknown";
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (calls.Count > 0)
        {
            message["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
            }).ToArray());
        }

        return new AiProviderTurn
        {
            HasMessage = hasMessage,
            MessageJson = message.ToJsonString(),
            Content = text.ToString(),
            ToolCalls = calls,
            Sources = sources,
            IncompleteReason = incompleteReason,
            InputTokens = ProviderJson.ReadInt(root, "usage", "input_tokens"),
            OutputTokens = ProviderJson.ReadInt(root, "usage", "output_tokens")
        };
    }

    private static JsonArray BuildInput(JsonArray messages, out string instructions)
    {
        var input = new JsonArray();
        var instructionParts = new List<string>();
        foreach (var node in messages.OfType<JsonObject>())
        {
            var role = ProviderJson.ReadNodeString(node, "role");
            var content = ProviderJson.ReadNodeString(node, "content");
            if (role is "system" or "developer")
            {
                if (!string.IsNullOrWhiteSpace(content)) instructionParts.Add(content);
                continue;
            }

            if (role == "tool")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = ProviderJson.ReadNodeString(node, "tool_call_id"),
                    ["output"] = content
                });
                continue;
            }

            if (role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(content))
                    input.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = content })
                    });
                if (node["tool_calls"] is JsonArray priorCalls)
                    foreach (var call in priorCalls.OfType<JsonObject>())
                    {
                        if (call["function"] is not JsonObject function) continue;
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = ProviderJson.ReadNodeString(call, "id"),
                            ["name"] = ProviderJson.ReadNodeString(function, "name"),
                            ["arguments"] = ProviderJson.ReadNodeString(function, "arguments")
                        });
                    }
                continue;
            }

            input.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = content })
            });
        }

        instructions = string.Join("\n\n", instructionParts);
        return input;
    }

    // The Responses API takes function tools in the flattened shape — {type, name, description,
    // parameters} — not the chat-completions {type, function:{...}} envelope.
    private static JsonArray BuildTools(bool enableWebSearch)
    {
        var tools = new JsonArray();
        foreach (var schema in AiFlowAgentTools.CreateToolSchemas().OfType<JsonObject>())
        {
            if (schema["function"] is not JsonObject function) continue;
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = function["name"]?.DeepClone(),
                ["description"] = function["description"]?.DeepClone(),
                ["parameters"] = function["parameters"]?.DeepClone()
            });
        }
        if (enableWebSearch) tools.Add(new JsonObject { ["type"] = "web_search" });
        return tools;
    }

    private static void AppendMessageText(JsonElement item, StringBuilder text, List<string> sources)
    {
        if (!item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array) return;
        foreach (var part in parts.EnumerateArray())
        {
            var partType = ProviderJson.ReadString(part, "type");
            if (string.Equals(partType, "output_text", StringComparison.Ordinal))
                text.Append(ProviderJson.ReadString(part, "text"));
            else if (string.Equals(partType, "refusal", StringComparison.Ordinal))
                text.Append(ProviderJson.ReadString(part, "refusal"));

            if (!part.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array) continue;
            foreach (var annotation in annotations.EnumerateArray())
            {
                if (!string.Equals(ProviderJson.ReadString(annotation, "type"), "url_citation", StringComparison.Ordinal)) continue;
                var url = ProviderJson.ReadString(annotation, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) continue;
                var title = ProviderJson.ReadString(annotation, "title").Trim();
                var source = title.Length == 0 ? $"Web search — {url}" : $"Web search — {title} ({url})";
                if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase)) sources.Add(source);
            }
        }
    }

    private static bool IsIncomplete(JsonElement root) =>
        root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
        string.Equals(status.GetString(), "incomplete", StringComparison.OrdinalIgnoreCase);
}

// Small JSON readers shared by both strategies: provider payloads are attacker-ish external
// text, so every read is kind-checked and returns a safe default instead of throwing.
internal static class ProviderJson
{
    public static string ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public static string ReadString(JsonElement element, string parent, string property) =>
        element.TryGetProperty(parent, out var parentElement) ? ReadString(parentElement, property) : string.Empty;

    public static string ReadNodeString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    public static int ReadInt(JsonElement element, string parent, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var parentElement) &&
        parentElement.ValueKind == JsonValueKind.Object && parentElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
}

// One transport for both API modes. The service above it owns the agent loop; this type owns
// endpoint derivation, request construction (delegated to the mode strategy), transient
// retries, provider auth, error mapping and response parsing.
internal sealed class AiProviderClient
{
    private const int MaxProviderAttempts = 3;
    private const int DefaultOutputTokens = 4096;
    private const int DefaultFlowBuildOutputTokens = 32768;
    private static readonly TimeSpan MaxProviderRetryDelay = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly int _flowBuildOutputTokens;

    public AiProviderClient(IHttpClientFactory httpClientFactory, ILogger logger, int flowBuildOutputTokens = DefaultFlowBuildOutputTokens)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _flowBuildOutputTokens = flowBuildOutputTokens > 0 ? flowBuildOutputTokens : DefaultFlowBuildOutputTokens;
    }

    public async Task<AiProviderTurn> SendAsync(Uri apiRoot, string providerType, string model, string apiKey, JsonArray messages,
        bool includeTools, bool isFlowBuild, AiProviderRequestOptions? options, CancellationToken cancellationToken,
        bool expectJsonDraft = false, int? flowBuildOutputTokens = null)
    {
        options ??= AiProviderRequestOptions.Default;
        var apiMode = AiApiModes.Normalize(options.ApiMode);
        var strategy = AiProviderApiStrategy.For(apiMode);

        var requestUrl = AiProviderEndpoints.DeriveRequestUrl(apiRoot.ToString(), apiMode);
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var endpoint))
            throw new AiFlowBuilderException(AiProviderEndpoints.Rule);

        // An Azure deployment URL carries the model in its path, so the request must not also
        // send a model name.
        var azureDeployment = providerType.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase) &&
            apiRoot.AbsolutePath.Contains("/openai/deployments/", StringComparison.OrdinalIgnoreCase);
        var context = new AiProviderRequestContext(
            Model: model,
            IncludeModel: !azureDeployment,
            Messages: messages,
            OutputTokens: isFlowBuild ? flowBuildOutputTokens ?? _flowBuildOutputTokens : DefaultOutputTokens,
            IncludeTools: includeTools,
            EnableWebSearch: includeTools && options.EnableWebSearch,
            ReasoningEffort: options.ReasoningEffort,
            IsFlowBuild: isFlowBuild,
            ExpectJsonDraft: expectJsonDraft);

        var body = strategy.BuildRequest(context);
        using var client = _httpClientFactory.CreateClient("AiProvider");
        using var response = await SendProviderRequestAsync(client, strategy, endpoint, body, providerType, apiKey, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("AI provider request failed with HTTP {StatusCode} ({ApiMode}).", (int)response.StatusCode, apiMode);
            var errorBody = await AiFlowAgentTools.ReadLimitedAsync(response.Content, 16_000, cancellationToken);
            throw new ProviderRequestException(response.StatusCode, ProviderErrorMessages.ReadProviderMessage(errorBody, apiKey));
        }

        var json = await AiFlowAgentTools.ReadLimitedAsync(response.Content, 2_000_000, cancellationToken);
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new AiFlowBuilderException($"The provider returned an invalid {strategy.ResponseLabel} response."); }

        using (document)
        {
            var turn = strategy.ParseResponse(document);
            if (turn.IncompleteReason is not null)
                _logger.LogWarning("AI provider stopped the {ApiMode} response early (reason: {Reason}).", apiMode, turn.IncompleteReason);
            if (turn.InputTokens > 0 || turn.OutputTokens > 0)
                _logger.LogInformation("AI provider used {InputTokens} input / {OutputTokens} output tokens ({ApiMode}).",
                    turn.InputTokens, turn.OutputTokens, apiMode);
            return turn;
        }
    }

    private async Task<HttpResponseMessage> SendProviderRequestAsync(HttpClient client, IAiProviderApiStrategy strategy,
        Uri endpoint, JsonObject body, string providerType, string apiKey, CancellationToken cancellationToken)
    {
        async Task<HttpResponseMessage> SendWithTransientRetriesAsync()
        {
            for (var attempt = 1; ; attempt++)
            {
                using var request = CreateProviderRequest(endpoint, body, providerType, apiKey);
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (attempt >= MaxProviderAttempts || !IsTransientProviderStatus(response.StatusCode))
                    return response;

                var delay = GetProviderRetryDelay(response, attempt);
                _logger.LogWarning("AI provider returned HTTP {StatusCode}; retrying attempt {Attempt} of {MaxAttempts} after {DelayMs}ms.",
                    (int)response.StatusCode, attempt + 1, MaxProviderAttempts, (int)delay.TotalMilliseconds);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
        }

        var response = await SendWithTransientRetriesAsync();
        if (!strategy.UsesCompletionTokenLimitFallback || response.StatusCode != HttpStatusCode.BadRequest || body.ContainsKey("max_completion_tokens"))
            return response;

        var errorBody = await AiFlowAgentTools.ReadLimitedAsync(response.Content, 16_000, cancellationToken);
        var error = ProviderErrorMessages.ReadProviderMessage(errorBody, apiKey);
        response.Content = new StringContent(errorBody, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
        if (!RequiresCompletionTokenRetry(response.StatusCode, error)) return response;

        response.Dispose();
        // Keep the same budget as the original request. The old fixed 4096 silently reduced
        // flow-build output after Azure rejected the legacy max_tokens alias.
        var outputTokens = body["max_tokens"] is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? parsed
            : DefaultOutputTokens;
        body.Remove("max_tokens");
        body["max_completion_tokens"] = outputTokens;
        return await SendWithTransientRetriesAsync();
    }

    private static HttpRequestMessage CreateProviderRequest(Uri endpoint, JsonObject body, string providerType, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (providerType.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation("api-key", apiKey);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static bool IsTransientProviderStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetProviderRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var requestedDelay = retryAfter?.Delta
            ?? (retryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1))));
        return requestedDelay < TimeSpan.Zero
            ? TimeSpan.Zero
            : requestedDelay > MaxProviderRetryDelay ? MaxProviderRetryDelay : requestedDelay;
    }

    private static bool RequiresCompletionTokenRetry(HttpStatusCode statusCode, string? message) =>
        statusCode == HttpStatusCode.BadRequest &&
        message is not null &&
        message.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase);
}
