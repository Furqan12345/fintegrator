using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Services;

public class TransportEngine : ITransportEngine
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionRepository _connectionRepository;
    private readonly IAuthenticationHandlerFactory _authFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly ResiliencePipeline<HttpResponseMessage> _resiliencePipeline;

    public TransportEngine(
        IHttpClientFactory httpClientFactory, 
        IConnectionRepository connectionRepository,
        IAuthenticationHandlerFactory authFactory,
        IEncryptionService encryptionService)
    {
        _httpClientFactory = httpClientFactory;
        _connectionRepository = connectionRepository;
        _authFactory = authFactory;
        _encryptionService = encryptionService;

        _resiliencePipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>().HandleResult(r =>
                    (int)r.StatusCode >= 500 ||
                    r.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                    (int)r.StatusCode == 429),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential
            })
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();
    }

    public async Task<(int StatusCode, string Response)> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = _httpClientFactory.CreateClient();

        var url = step.EndpointUrl;
        AuthType authType = step.AuthType;
        string configJson = string.Empty;
        Guid? resolvedConnectionId = null;

        // If a Connection is specified, merge its base URL and Auth configurations
        if (connectionId.HasValue && connectionId.Value != Guid.Empty)
        {
            var connection = await _connectionRepository.GetByIdAsync(connectionId.Value);
            if (connection != null)
            {
                if (!string.IsNullOrWhiteSpace(connection.BaseUrl))
                {
                    // Ensure proper URL concatenation
                    url = connection.BaseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
                }

                authType = connection.AuthType;
                configJson = await _encryptionService.DecryptAsync(connection.AuthConfigJson);
                resolvedConnectionId = connection.Id;
            }
        }
        else
        {
            // Fallback to step level inline auth if no connection is provided
            var stepAuthConfigJson = await RevealAsync(step.AuthConfigJson);
            var stepAuthToken = await RevealAsync(step.AuthToken);
            var stepAuthUsername = await RevealAsync(step.AuthUsername);
            var stepAuthPassword = await RevealAsync(step.AuthPassword);

            if (!string.IsNullOrWhiteSpace(stepAuthConfigJson))
            {
                configJson = stepAuthConfigJson;
            }
            else if (step.AuthType == AuthType.Bearer && !string.IsNullOrWhiteSpace(stepAuthToken))
            {
                configJson = SerializeAuthConfig(new Dictionary<string, string>
                {
                    ["token"] = stepAuthToken
                });
            }
            else if (step.AuthType == AuthType.Basic && !string.IsNullOrWhiteSpace(stepAuthUsername))
            {
                configJson = SerializeAuthConfig(new Dictionary<string, string>
                {
                    ["username"] = stepAuthUsername,
                    ["password"] = stepAuthPassword
                });
            }
            else if (step.AuthType == AuthType.ApiKey && !string.IsNullOrWhiteSpace(stepAuthUsername))
            {
                configJson = SerializeAuthConfig(new Dictionary<string, string>
                {
                    ["headerName"] = stepAuthUsername,
                    ["apiKey"] = stepAuthToken
                });
            }
            else if (step.AuthType == AuthType.OAuth2RefreshToken || step.AuthType == AuthType.OAuth2AuthCode)
            {
                configJson = SerializeAuthConfig(new Dictionary<string, string>
                {
                    ["tokenUrl"] = stepAuthUsername,
                    ["refreshToken"] = stepAuthPassword,
                    ["accessToken"] = stepAuthToken
                });
            }
        }

        var packetDetails = ApiPacketDetails.FromStepConfig(step.StepConfig);
        url = AppendQueryParams(url, packetDetails.QueryParams);

        var authContext = new AuthenticationContext
        {
            ConfigJson = configJson,
            ConnectionId = resolvedConnectionId
        };

        payload = string.IsNullOrWhiteSpace(payload) ? packetDetails.RequestBody : payload;

        HttpRequestMessage BuildRequest()
        {
            var message = new HttpRequestMessage(new HttpMethod(step.HttpMethod), url);

            foreach (var header in packetDetails.Headers)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) &&
                    !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(payload) &&
                (step.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                 step.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                 step.HttpMethod.Equals("PATCH", StringComparison.OrdinalIgnoreCase)))
            {
                var contentType = string.IsNullOrWhiteSpace(packetDetails.ContentType)
                    ? "application/json"
                    : packetDetails.ContentType;
                message.Content = new StringContent(payload, Encoding.UTF8, contentType);
            }

            return message;
        }

        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            return await _resiliencePipeline.ExecuteAsync(async attemptToken =>
            {
                // We need to clone the request because HttpClient disposes the request after SendAsync
                var clonedRequest = await CloneHttpRequestMessageAsync(request);
                return await client.SendAsync(clonedRequest, attemptToken);
            }, cancellationToken);
        }

        var authHandler = _authFactory.GetHandler(authType);

        var initialRequest = BuildRequest();
        await authHandler.AuthenticateAsync(initialRequest, authContext);

        var response = await SendAsync(initialRequest);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            (authType == AuthType.OAuth2ClientCredentials ||
             authType == AuthType.OAuth2RefreshToken ||
             authType == AuthType.OAuth2AuthCode))
        {
            authContext.ForceRefresh = true;
            var retryRequest = BuildRequest();
            await authHandler.AuthenticateAsync(retryRequest, authContext);
            response = await SendAsync(retryRequest);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ((int)response.StatusCode, content);
    }

    private static string SerializeAuthConfig(Dictionary<string, string> values)
    {
        return JsonSerializer.Serialize(values);
    }

    private async Task<string> RevealAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return await _encryptionService.DecryptAsync(value);
        }
        catch (Exception)
        {
            return value;
        }
    }

    private async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        
        if (req.Content != null)
        {
            var bytes = await req.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            
            if (req.Content.Headers != null)
            {
                foreach (var h in req.Content.Headers)
                {
                    clone.Content.Headers.Add(h.Key, h.Value);
                }
            }
        }

        clone.Version = req.Version;

        foreach (var prop in req.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(prop.Key), prop.Value);
        }

        foreach (var header in req.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static string AppendQueryParams(string url, IReadOnlyDictionary<string, string> queryParams)
    {
        if (queryParams.Count == 0)
        {
            return url;
        }

        var query = string.Join("&", queryParams
            .Where(param => !string.IsNullOrWhiteSpace(param.Key))
            .Select(param => $"{Uri.EscapeDataString(param.Key)}={Uri.EscapeDataString(param.Value ?? string.Empty)}"));

        if (string.IsNullOrWhiteSpace(query))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url + (url.Contains('?') ? "&" : "?") + query;
        }

        var builder = new UriBuilder(uri);
        var existingQuery = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(existingQuery)
            ? query
            : $"{existingQuery}&{query}";
        return builder.Uri.ToString();
    }

    private sealed class ApiPacketDetails
    {
        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> QueryParams { get; init; } = new Dictionary<string, string>();
        public string RequestBody { get; init; } = string.Empty;
        public string ContentType { get; init; } = "application/json";

        public static ApiPacketDetails FromStepConfig(string? stepConfig)
        {
            if (string.IsNullOrWhiteSpace(stepConfig))
            {
                return new ApiPacketDetails();
            }

            try
            {
                using var document = JsonDocument.Parse(stepConfig);
                if (!document.RootElement.TryGetProperty("apiPacket", out var packet))
                {
                    return new ApiPacketDetails();
                }

                return new ApiPacketDetails
                {
                    Headers = ReadObjectFromJsonString(packet, "headersJson"),
                    QueryParams = ReadObjectFromJsonString(packet, "queryParamsJson"),
                    RequestBody = ReadString(packet, "requestBody"),
                    ContentType = ReadString(packet, "contentType", "application/json")
                };
            }
            catch (JsonException)
            {
                return new ApiPacketDetails();
            }
        }

        private static string ReadString(JsonElement packet, string propertyName, string fallback = "")
        {
            if (!packet.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }

            return property.GetString() ?? fallback;
        }

        private static IReadOnlyDictionary<string, string> ReadObjectFromJsonString(JsonElement packet, string propertyName)
        {
            var json = ReadString(packet, propertyName, "{}");
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, string>();
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new Dictionary<string, string>();
                }

                return document.RootElement.EnumerateObject()
                    .Where(property => !string.IsNullOrWhiteSpace(property.Name))
                    .ToDictionary(
                        property => property.Name,
                        property => property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString() ?? string.Empty
                            : property.Value.GetRawText());
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
