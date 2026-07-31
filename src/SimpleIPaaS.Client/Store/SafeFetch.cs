using System;
using System.Net.Http;
using System.Net.Http.Json;
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

    public static async Task<string> DescribeFailureAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsDto>();
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            if (!string.IsNullOrWhiteSpace(problem?.Title))
            {
                return problem.Title;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private sealed class ProblemDetailsDto
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
    }
}
