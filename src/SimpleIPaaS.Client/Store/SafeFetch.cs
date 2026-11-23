using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Fluxor;

namespace SimpleIPaaS.Client.Store;

public static class SafeFetch
{
    public static async Task<T?> GetAsync<T>(HttpClient http, string url, IDispatcher dispatcher, string description)
    {
        try
        {
            return await http.GetFromJsonAsync<T>(url);
        }
        catch (Exception)
        {
            dispatcher.Dispatch(new ShowToastAction
            {
                Message = $"Could not load {description}. The API may be unavailable.",
                Level = "error"
            });
            return default;
        }
    }

    public static async Task<T?> SendAsync<T>(
        Func<Task<HttpResponseMessage>> send,
        IDispatcher dispatcher,
        string description)
    {
        HttpResponseMessage response;
        try
        {
            response = await send();
        }
        catch (Exception)
        {
            dispatcher.Dispatch(new ShowToastAction
            {
                Message = $"Could not save {description}. The API may be unavailable.",
                Level = "error"
            });
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            dispatcher.Dispatch(new ShowToastAction
            {
                Message = await DescribeFailureAsync(response, $"Could not save {description}."),
                Level = "error"
            });
            return default;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (Exception)
        {
            dispatcher.Dispatch(new ShowToastAction
            {
                Message = $"Saved {description}, but the response could not be read.",
                Level = "warning"
            });
            return default;
        }
    }

    // The API's own actionable text wins over the caller's generic fallback: StorageSettings uses
    // { "message": ... }, ProblemDetails uses detail/title, and validation problems carry an errors
    // map. Dropping that text is how "Could not save the storage settings." hid the real reason.
    public static async Task<string> DescribeFailureAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }

            var message =
                ReadString(root, "message")
                ?? ReadString(root, "detail")
                ?? ReadString(root, "title")
                ?? ReadFirstValidationError(root);

            return string.IsNullOrWhiteSpace(message) ? fallback : message;
        }
        catch
        {
            // A non-JSON error body (a proxy's HTML page, for instance) keeps the fallback.
        }

        return fallback;
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? ReadFirstValidationError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var field in errors.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in field.Value.EnumerateArray())
                {
                    var text = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        return null;
    }
}
