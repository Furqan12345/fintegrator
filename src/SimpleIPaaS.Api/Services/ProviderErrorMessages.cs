using System;
using System.Net;
using System.Text.Json;

namespace SimpleIPaaS.Api.Services;

// One mapping from provider failures to actionable user-facing text. Shared by the
// single-shot build worker and the chat worker so both surfaces explain errors the
// same way.
//
// Every branch carries the provider's own message when there is one (already redacted of
// the API key and capped at 800 characters by AiFlowBuilderService.ReadProviderErrorMessage),
// and every branch states the numeric HTTP status so an unmapped status (402, 405, 409,
// 422, ...) is never reported as a generic "returned an error".
public static class ProviderErrorMessages
{
    // Provider bodies are attacker-ish external text: keep only the first line and cap the length.
    private const int MaxProviderDetailChars = 400;

    private static readonly string[] ErrorFallbackProperties = { "code", "type" };

    // A rotated Encryption:Key makes every stored ciphertext undecryptable. The fix is operator
    // work (re-enter the secret), so the API returns this as 409 and the workers record it
    // verbatim instead of a generic "failed unexpectedly".
    public const string StoredCredentialUnreadable =
        "The stored API key cannot be decrypted. The Encryption:Key may have been rotated; re-enter the API key for this provider.";

    public static string Describe(ProviderRequestException ex)
    {
        var detail = Compact(ex.ProviderMessage);
        var status = (int)ex.StatusCode;
        var message = ex.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The provider rejected the API key. Check the saved key.",
            HttpStatusCode.Forbidden => "The provider key does not have permission to use this model.",
            HttpStatusCode.NotFound => "The provider endpoint or model was not found. Check the endpoint and model name.",
            (HttpStatusCode)429 => "The provider rate limit or quota was reached. Try again later.",
            HttpStatusCode.BadRequest => "The provider rejected the request.",
            var code when (int)code >= 500 => "The AI provider is temporarily unavailable. Try again shortly.",
            _ => "The AI provider could not complete the request. Check its endpoint, model and configuration."
        };
        // Every status carries its numeric HTTP code — mapped (400/401/403/404/429/5xx) and
        // unmapped (402, 405, 409, 422, ...) alike — plus the provider's own message when there
        // is one, so a failure is never reported as a bare generic sentence.
        return WithDetail($"HTTP {status}: {message}", detail);
    }

    private static string WithDetail(string message, string? detail) =>
        detail == null ? message : message + " Provider message: " + detail;

    // The provider's own error text, from either API shape:
    //   Chat Completions: {"error":{"message":"...","type":"...","code":"..."}} (also seen as
    //                     {"error":"text"}, {"error":[{...}]} and a bare {"message":"..."})
    //   Responses:        {"error":{"message":"...","type":"...","code":"..."}} — the message is
    //                     preferred, then the type/code, so a bare code is still surfaced.
    // The API key is redacted and the result capped, because a provider can echo the request
    // back at us. Returns null when there is nothing readable.
    public static string? ReadProviderMessage(string? body, string? secretToRedact)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement message = default;
            var hasError = root.TryGetProperty("error", out var error);
            if (hasError)
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("message", out var nestedMessage)) message = nestedMessage;
                }
                else if (error.ValueKind is JsonValueKind.String or JsonValueKind.Array)
                {
                    message = error;
                }
            }

            if (message.ValueKind is not (JsonValueKind.String or JsonValueKind.Array) &&
                root.TryGetProperty("message", out var topLevelMessage))
                message = topLevelMessage;

            var value = message.ValueKind switch
            {
                JsonValueKind.String => message.GetString()?.Trim(),
                JsonValueKind.Array => message.ToString().Trim(),
                _ => null
            };
            // A Responses error can carry only the machine-readable parts; the code is more
            // actionable than the type ("invalid_api_version" beats "invalid_request_error").
            if (string.IsNullOrWhiteSpace(value) && hasError && error.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in ErrorFallbackProperties)
                {
                    if (error.TryGetProperty(property, out var fallback) && fallback.ValueKind == JsonValueKind.String)
                    {
                        value = fallback.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value)) break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(value)) return null;

            if (!string.IsNullOrEmpty(secretToRedact))
                value = value.Replace(secretToRedact, "[redacted]", StringComparison.Ordinal);
            return value.Length <= 800 ? value : value[..800];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // First line only, trimmed, with an explicit marker when the provider text was truncated.
    private static string? Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= MaxProviderDetailChars
            ? singleLine
            : singleLine[..MaxProviderDetailChars] + " [provider message truncated]";
    }
}
