using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleIPaaS.Shared.Models;

// The two provider request shapes fusetor.com speaks. Chat Completions is the safe
// default: every existing provider row, and every self-hosted OpenAI-compatible server
// (Ollama, LM Studio, vLLM, LiteLLM), keeps working untouched. Responses is the newer
// OpenAI API surface and is what a new OpenAI (api.openai.com) provider starts on.
//
// These helpers live in Shared because the API validates/normalizes with them and the
// Blazor wizard validates with the very same ones — the client must never block a value
// the server accepts.
public static class AiApiModes
{
    public const string ChatCompletions = "ChatCompletions";
    public const string Responses = "Responses";

    public static string Normalize(string? apiMode) => IsResponses(apiMode) ? Responses : ChatCompletions;

    public static bool IsResponses(string? apiMode) => string.Equals(apiMode, Responses, StringComparison.OrdinalIgnoreCase);

    // Default for a provider that was saved without an explicit choice: OpenAI's own host
    // speaks Responses (its recommended API); self-hosted OpenAI-compatible servers and
    // Azure OpenAI stay on Chat Completions, and Azure remains operator-selectable.
    public static string DefaultFor(string? providerType, string? endpoint)
        => string.Equals(providerType, "AzureOpenAI", StringComparison.OrdinalIgnoreCase) || !IsOpenAiHost(endpoint)
            ? ChatCompletions
            : Responses;

    private static bool IsOpenAiHost(string? endpoint)
    {
        if (!Uri.TryCreate((endpoint ?? string.Empty).Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase);
    }
}

// Reasoning effort values the Responses API accepts. Nullable by design: "not set" means
// the request carries no reasoning object at all, which is what every non-reasoning model
// (and every Chat Completions provider) expects.
public static class AiReasoningEfforts
{
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    public static readonly IReadOnlyList<string> All = new[] { Minimal, Low, Medium, High };

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return All.FirstOrDefault(effort => string.Equals(effort, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

// One endpoint rule and one normalizer, shared by save, validation and use.
//
// Operators paste whatever their provider shows them: the API root ("https://api.openai.com/v1"),
// a full chat-completions URL (".../v1/chat/completions"), a Responses URL (".../v1/responses"),
// an Azure deployment path with its api-version query, or a root without /v1 at all (a plain
// host, LiteLLM's "/v1", Google's "/v1beta/openai"). All of them reduce to the same API root,
// which is what the app stores; the request URL is derived from that root per API mode, so
// switching mode later never needs the endpoint re-entered.
public static class AiProviderEndpoints
{
    public const string ChatCompletionsPath = "/chat/completions";
    public const string ResponsesPath = "/responses";

    // The legacy text-completions API is a different endpoint family; an explicit
    // ".../completions" (not "/chat/completions") is always a paste mistake.
    private const string LegacyCompletionsPath = "/completions";

    public const string Rule =
        "Use the provider API root or a full endpoint — for example https://api.openai.com/v1, https://api.openai.com/v1/chat/completions " +
        "or https://api.openai.com/v1/responses. The app appends /chat/completions or /responses itself. " +
        "Use an absolute http(s) URL with no credentials and no fragment. Any port is allowed. " +
        "Plain HTTP is allowed for localhost and private-network (self-hosted) addresses; public hosts must use HTTPS.";

    // Strips a trailing /chat/completions, /responses or "/" to obtain the API root. The
    // query string (Azure's ?api-version=...) and the rest of the path are preserved.
    public static string NormalizeRoot(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        var value = endpoint.Trim();
        var queryIndex = value.IndexOf('?');
        var address = (queryIndex < 0 ? value : value[..queryIndex]).TrimEnd('/');
        if (address.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase))
            address = address[..^ChatCompletionsPath.Length];
        else if (address.EndsWith(ResponsesPath, StringComparison.OrdinalIgnoreCase))
            address = address[..^ResponsesPath.Length];
        address = address.TrimEnd('/');
        return address + (queryIndex < 0 ? string.Empty : value[queryIndex..]);
    }

    // {root}/chat/completions or {root}/responses, with Azure's query string kept at the end
    // (/openai/deployments/{deployment}/chat/completions?api-version=...).
    public static string DeriveRequestUrl(string? endpoint, string? apiMode)
    {
        var root = NormalizeRoot(endpoint);
        if (root.Length == 0) return string.Empty;
        var queryIndex = root.IndexOf('?');
        var address = queryIndex < 0 ? root : root[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : root[queryIndex..];
        return address + (AiApiModes.IsResponses(apiMode) ? ResponsesPath : ChatCompletionsPath) + query;
    }

    // Shared structural rule. Returns the rule text when the value cannot be used, or null
    // when it is acceptable. Host safety (HTTPS for public hosts, reserved addresses) is
    // enforced server-side only, because it needs DNS; this check is deliberately weaker so
    // client-side validation can never reject something the server would accept.
    //
    // apiMode is taken for mode-aware validation: an explicit /responses URL in
    // ChatCompletions mode (or the other way round) never has to be rejected, because the
    // suffix is stripped to the same root and the mode decides what is appended. Nothing
    // mode-specific is rejected here — the mismatch is normalized silently.
    public static string? Validate(string? endpoint, string? apiMode = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 1200) return Rule;
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)) return Rule;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return Rule;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return Rule;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(LegacyCompletionsPath, StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase)) return Rule;
        _ = apiMode;
        return null;
    }
}
