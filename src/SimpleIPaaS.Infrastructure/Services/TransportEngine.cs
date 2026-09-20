using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
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
    private readonly ILogger<TransportEngine> _logger;

    public TransportEngine(
        IHttpClientFactory httpClientFactory, 
        IConnectionRepository connectionRepository,
        IAuthenticationHandlerFactory authFactory,
        IEncryptionService encryptionService,
        ILogger<TransportEngine> logger)
    {
        _httpClientFactory = httpClientFactory;
        _connectionRepository = connectionRepository;
        _authFactory = authFactory;
        _encryptionService = encryptionService;
        _logger = logger;

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

    public async Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default, FlowTestContext? testContext = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = _httpClientFactory.CreateClient();
        var operationStartedAt = Stopwatch.GetTimestamp();
        _logger.LogInformation(new EventId(1500, "dependency.http.started"), "HTTP card {CardId} started {HttpMethod} {Endpoint} (connection {ConnectionId})", step.Id, step.HttpMethod, SafeEndpoint(step.EndpointUrl), connectionId);

        var url = step.EndpointUrl;

        if (testContext != null && !testContext.HasHttpFixture(step.Id))
            throw new InvalidOperationException($"HTTP card '{step.Id}' has no response fixture. Add a response in Test mode before running the scenario.");

        // Test fixtures short-circuit before connection lookup, secret decryption, authentication,
        // retries, and network I/O. The normal packet shape is still returned for debug logs.
        if (testContext?.TryTakeHttpResponse(step.Id, out var earlyFixtureResponse) == true)
        {
            var fixturePacketDetails = ApiPacketDetails.FromStepConfig(step.StepConfig);
            var fixtureUrl = AppendQueryParams(url, fixturePacketDetails.QueryParams);
            var fixturePayload = string.IsNullOrWhiteSpace(payload) ? fixturePacketDetails.RequestBody : payload;
            var fixtureStartedAt = DateTime.UtcNow;
            var fixtureCompletedAt = DateTime.UtcNow;
            var fixtureRequestHeaders = fixturePacketDetails.Headers.ToDictionary(pair => pair.Key, pair => new[] { pair.Value }, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation(new EventId(1502, "dependency.http.fixture"), "HTTP card {CardId} used a test fixture with status {StatusCode} (connection {ConnectionId})", step.Id, earlyFixtureResponse.StatusCode, connectionId);
            var fixtureAttempt = new TransportAttempt(
                earlyFixtureResponse.StatusCode,
                earlyFixtureResponse.Response,
                earlyFixtureResponse.Headers,
                fixtureUrl,
                step.HttpMethod,
                fixtureRequestHeaders,
                fixturePayload ?? string.Empty,
                fixtureStartedAt,
                fixtureCompletedAt);
            return new TransportResponse(earlyFixtureResponse.StatusCode, earlyFixtureResponse.Response, earlyFixtureResponse.Headers, fixtureUrl)
            {
                HttpMethod = step.HttpMethod,
                RequestHeaders = fixtureRequestHeaders,
                RequestBody = fixturePayload ?? string.Empty,
                StartedAt = fixtureStartedAt,
                CompletedAt = fixtureCompletedAt,
                Attempts = new[] { fixtureAttempt }
            };
        }
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
            ConnectionId = resolvedConnectionId,
            RequestMetadataJson = step.StepConfig
        };

        payload = string.IsNullOrWhiteSpace(payload) ? packetDetails.RequestBody : payload;

        if (testContext?.TryTakeHttpResponse(step.Id, out var fixtureResponse) == true)
        {
            var startedAt = DateTime.UtcNow;
            var completedAt = DateTime.UtcNow;
            var requestHeaders = packetDetails.Headers.ToDictionary(pair => pair.Key, pair => new[] { pair.Value }, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation(new EventId(1502, "dependency.http.fixture"), "HTTP card {CardId} used a test fixture with status {StatusCode} (connection {ConnectionId})", step.Id, fixtureResponse.StatusCode, resolvedConnectionId);
            var fixtureAttempt = new TransportAttempt(
                fixtureResponse.StatusCode,
                fixtureResponse.Response,
                fixtureResponse.Headers,
                url,
                step.HttpMethod,
                requestHeaders,
                payload ?? string.Empty,
                startedAt,
                completedAt);
            return new TransportResponse(fixtureResponse.StatusCode, fixtureResponse.Response, fixtureResponse.Headers, url)
            {
                HttpMethod = step.HttpMethod,
                RequestHeaders = requestHeaders,
                RequestBody = payload ?? string.Empty,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Attempts = new[] { fixtureAttempt }
            };
        }

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

        var authHandler = _authFactory.GetHandler(authType);

        TransportAttempt? lastAttempt = null;

        var attemptNumber = 0;

        async Task<HttpResponseMessage> SendAsync()
        {
            return await _resiliencePipeline.ExecuteAsync(async attemptToken =>
            {
                using var request = BuildRequest();
                var retryAttempt = attemptNumber++;
                await authHandler.AuthenticateAsync(request, authContext);
                var startedAt = DateTime.UtcNow;
                var requestBody = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(attemptToken);
                var requestHeaders = SnapshotHeaders(request);
                var response = await client.SendAsync(request, attemptToken);
                var completedAt = DateTime.UtcNow;
                var responseHeaders = SnapshotHeaders(response);
                _logger.LogInformation(new EventId(1501, "dependency.http.attempt"), "HTTP attempt for card {CardId} returned {StatusCode} in {DurationMs}ms ({ResponseBytes} bytes, retry {RetryAttempt}, connection {ConnectionId})", step.Id, (int)response.StatusCode, (long)(completedAt - startedAt).TotalMilliseconds, response.Content.Headers.ContentLength ?? 0, retryAttempt, resolvedConnectionId);
                lastAttempt = new TransportAttempt(
                    (int)response.StatusCode,
                    await response.Content.ReadAsStringAsync(attemptToken),
                    responseHeaders,
                    request.RequestUri?.ToString() ?? url,
                    request.Method.Method,
                    requestHeaders,
                    requestBody,
                    startedAt,
                    completedAt);
                return response;
            }, cancellationToken);
        }

        var response = await SendAsync();

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            (authType == AuthType.OAuth2ClientCredentials ||
             authType == AuthType.OAuth2RefreshToken ||
             authType == AuthType.OAuth2AuthCode ||
             authType == AuthType.AmazonSpApi))
        {
            authContext.ForceRefresh = true;
            response = await SendAsync();
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.SelectMany(header => header.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        var attempt = lastAttempt ?? new TransportAttempt(
            (int)response.StatusCode,
            content,
            headers,
            url,
            step.HttpMethod,
            new Dictionary<string, string[]>(),
            payload ?? string.Empty,
            DateTime.UtcNow,
            DateTime.UtcNow);
        _logger.LogInformation(new EventId(1503, "dependency.http.completed"), "HTTP card {CardId} completed with {StatusCode} in {DurationMs}ms (connection {ConnectionId})", step.Id, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(operationStartedAt).TotalMilliseconds, resolvedConnectionId);
        return new TransportResponse((int)response.StatusCode, content, headers, url)
        {
            HttpMethod = attempt.HttpMethod,
            RequestHeaders = attempt.RequestHeaders,
            RequestBody = attempt.RequestBody,
            StartedAt = attempt.StartedAt,
            CompletedAt = attempt.CompletedAt,
            Attempts = new[] { attempt }
        };
    }

    private static Dictionary<string, string[]> SnapshotHeaders(HttpRequestMessage request)
    {
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => RedactHeader(header.Key, header.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        return headers;
    }

    private static Dictionary<string, string[]> SnapshotHeaders(HttpResponseMessage response)
    {
        return response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> RedactHeader(string name, IEnumerable<string> values)
    {
        var sensitive = name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase);
        return sensitive ? new[] { "[REDACTED]" } : values;
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

    private static string SafeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        var queryIndex = endpoint.IndexOf('?', StringComparison.Ordinal);
        return queryIndex >= 0 ? endpoint[..queryIndex] : endpoint;
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
