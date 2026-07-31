using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Services.Auth;

public abstract class AuthenticationHandlerBase : IAuthenticationHandler
{
    public Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        return AuthenticateAsync(request, new AuthenticationContext { ConfigJson = configJson });
    }

    public abstract Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context);

    protected static void AppendQueryParameter(HttpRequestMessage request, string name, string value)
    {
        var uri = request.RequestUri;
        if (uri == null) return;

        var queryString = string.Empty;

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            queryString = uri.Query.TrimStart('?');
            queryString += "&";
        }

        queryString += $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

        var newUri = new UriBuilder(uri.Scheme, uri.Host, uri.Port, uri.AbsolutePath, "?" + queryString);
        request.RequestUri = newUri.Uri;
    }
}

internal static class OAuthTokenCache
{
    private static readonly ConcurrentDictionary<string, CachedToken> Tokens = new();
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    internal sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    internal static string BuildKey(Guid? connectionId, string configJson)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson ?? string.Empty)));
        return connectionId.HasValue ? $"{connectionId.Value}:{hash}" : $"inline:{hash}";
    }

    internal static bool TryGet(string key, out string accessToken)
    {
        accessToken = string.Empty;
        if (Tokens.TryGetValue(key, out var cached))
        {
            if (cached.ExpiresAt - ExpirySkew > DateTimeOffset.UtcNow)
            {
                accessToken = cached.AccessToken;
                return true;
            }
            Tokens.TryRemove(key, out _);
        }
        return false;
    }

    internal static void Store(string key, string accessToken, int expiresInSeconds)
    {
        Tokens[key] = new CachedToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds));
    }

    internal static void Invalidate(string key)
    {
        Tokens.TryRemove(key, out _);
    }
}

internal static class OAuthTokenClient
{
    internal static async Task<JObject> RequestTokenAsync(IHttpClientFactory httpClientFactory, string tokenUrl, Dictionary<string, string> form)
    {
        var client = httpClientFactory.CreateClient();

        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        tokenRequest.Content = new FormUrlEncodedContent(form);
        tokenRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        var tokenResponse = await client.SendAsync(tokenRequest);
        var responseContent = await tokenResponse.Content.ReadAsStringAsync();

        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token endpoint request failed: {tokenResponse.StatusCode} - {responseContent}");
        }

        return JObject.Parse(responseContent);
    }
}

public class BasicAuthHandler : AuthenticationHandlerBase
{
    public override Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        var config = JObject.Parse(context.ConfigJson);
        var username = config["username"]?.ToString();
        var password = config["password"]?.ToString();

        var byteArray = Encoding.ASCII.GetBytes($"{username}:{password}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

        return Task.CompletedTask;
    }
}

public class ApiKeyAuthHandler : AuthenticationHandlerBase
{
    public override Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        var config = JObject.Parse(context.ConfigJson);
        var headerName = config["headerName"]?.ToString() ?? "X-Api-Key";
        var apiKey = config["apiKey"]?.ToString() ?? string.Empty;
        var placement = config["placement"]?.ToString() ?? "header";

        if (string.Equals(placement, "query", StringComparison.OrdinalIgnoreCase))
        {
            AppendQueryParameter(request, headerName, apiKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation(headerName, apiKey);
        }

        return Task.CompletedTask;
    }
}

public class BearerAuthHandler : AuthenticationHandlerBase
{
    public override Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        var config = JObject.Parse(context.ConfigJson);
        var token = config["token"]?.ToString();

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return Task.CompletedTask;
    }
}

public class OAuth2ClientCredentialsHandler : AuthenticationHandlerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OAuth2ClientCredentialsHandler(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override async Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        var config = JObject.Parse(context.ConfigJson);
        var tokenUrl = config["tokenUrl"]?.ToString();

        if (string.IsNullOrWhiteSpace(tokenUrl))
        {
            var staticToken = config["accessToken"]?.ToString();
            if (string.IsNullOrWhiteSpace(staticToken))
            {
                throw new InvalidOperationException("TokenUrl is required for OAuth2 Client Credentials flow");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staticToken);
            return;
        }

        var cacheKey = OAuthTokenCache.BuildKey(context.ConnectionId, context.ConfigJson);

        if (context.ForceRefresh)
        {
            OAuthTokenCache.Invalidate(cacheKey);
        }
        else if (OAuthTokenCache.TryGet(cacheKey, out var cachedToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cachedToken);
            return;
        }

        var form = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "client_id", config["clientId"]?.ToString() ?? string.Empty },
            { "client_secret", config["clientSecret"]?.ToString() ?? string.Empty }
        };

        var scope = config["scope"]?.ToString();
        if (!string.IsNullOrWhiteSpace(scope))
        {
            form["scope"] = scope;
        }

        var responseJson = await OAuthTokenClient.RequestTokenAsync(_httpClientFactory, tokenUrl, form);
        var accessToken = responseJson["access_token"]?.ToString();

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Token response did not contain access_token");
        }

        var expiresIn = responseJson["expires_in"]?.ToObject<int?>() ?? 3600;
        OAuthTokenCache.Store(cacheKey, accessToken, expiresIn);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }
}

public class OAuth2RefreshTokenHandler : AuthenticationHandlerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionRepository _connectionRepository;
    private readonly IEncryptionService _encryptionService;
    private readonly bool _useAuthorizationHeader;

    public OAuth2RefreshTokenHandler(
        IHttpClientFactory httpClientFactory,
        IConnectionRepository connectionRepository,
        IEncryptionService encryptionService,
        bool useAuthorizationHeader = false)
    {
        _httpClientFactory = httpClientFactory;
        _connectionRepository = connectionRepository;
        _encryptionService = encryptionService;
        _useAuthorizationHeader = useAuthorizationHeader;
    }

    public override async Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        var config = JObject.Parse(context.ConfigJson);
        var tokenUrl = config["tokenUrl"]?.ToString();
        var refreshToken = config["refreshToken"]?.ToString();
        var staticAccessToken = config["accessToken"]?.ToString();
        var useHeader = _useAuthorizationHeader || string.Equals(config["placement"]?.ToString(), "header", StringComparison.OrdinalIgnoreCase);

        var cacheKey = OAuthTokenCache.BuildKey(context.ConnectionId, context.ConfigJson);

        if (context.ForceRefresh)
        {
            OAuthTokenCache.Invalidate(cacheKey);
        }
        else
        {
            if (OAuthTokenCache.TryGet(cacheKey, out var cachedToken))
            {
                ApplyAccessToken(request, cachedToken, useHeader);
                return;
            }

            if (!string.IsNullOrWhiteSpace(staticAccessToken))
            {
                ApplyAccessToken(request, staticAccessToken, useHeader);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(tokenUrl) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("TokenUrl and RefreshToken are required for OAuth2 Refresh Token flow");
        }

        var form = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", refreshToken }
        };

        var clientId = config["clientId"]?.ToString();
        var clientSecret = config["clientSecret"]?.ToString();
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            form["client_id"] = clientId;
        }
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }

        var responseJson = await OAuthTokenClient.RequestTokenAsync(_httpClientFactory, tokenUrl, form);
        var newAccessToken = responseJson["access_token"]?.ToString();

        if (string.IsNullOrWhiteSpace(newAccessToken))
        {
            throw new InvalidOperationException("Token response did not contain access_token");
        }

        var expiresIn = responseJson["expires_in"]?.ToObject<int?>() ?? 3600;
        OAuthTokenCache.Store(cacheKey, newAccessToken, expiresIn);

        var rotatedRefreshToken = responseJson["refresh_token"]?.ToString();
        if (!string.IsNullOrWhiteSpace(rotatedRefreshToken)
            && !string.Equals(rotatedRefreshToken, refreshToken, StringComparison.Ordinal)
            && context.ConnectionId.HasValue)
        {
            await PersistRotatedTokensAsync(context.ConnectionId.Value, config, rotatedRefreshToken, newAccessToken);
        }

        ApplyAccessToken(request, newAccessToken, useHeader);
    }

    private static void ApplyAccessToken(HttpRequestMessage request, string accessToken, bool useHeader)
    {
        if (useHeader)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        else
        {
            AppendQueryParameter(request, "access_token", accessToken);
        }
    }

    private async Task PersistRotatedTokensAsync(Guid connectionId, JObject config, string rotatedRefreshToken, string accessToken)
    {
        var connection = await _connectionRepository.GetByIdAsync(connectionId);
        if (connection == null) return;

        config["refreshToken"] = rotatedRefreshToken;
        config["accessToken"] = accessToken;

        connection.AuthConfigJson = await _encryptionService.EncryptAsync(config.ToString(Newtonsoft.Json.Formatting.None));
        connection.UpdatedAt = DateTime.UtcNow;

        await _connectionRepository.UpdateAsync(connection);
    }
}

public class OAuth2AuthCodeHandler : OAuth2RefreshTokenHandler
{
    public OAuth2AuthCodeHandler(
        IHttpClientFactory httpClientFactory,
        IConnectionRepository connectionRepository,
        IEncryptionService encryptionService)
        : base(httpClientFactory, connectionRepository, encryptionService, useAuthorizationHeader: true)
    {
    }
}

public class CustomAuthHandler : AuthenticationHandlerBase
{
    public override Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        var config = JObject.Parse(context.ConfigJson);

        if (config["headers"] is JArray headers)
        {
            foreach (var header in headers)
            {
                var name = header["name"]?.ToString();
                var value = header["value"]?.ToString() ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return Task.CompletedTask;
    }
}

public class NoAuthHandler : AuthenticationHandlerBase
{
    public override Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        return Task.CompletedTask;
    }
}

public class AuthenticationHandlerFactory : IAuthenticationHandlerFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionRepository _connectionRepository;
    private readonly IEncryptionService _encryptionService;

    public AuthenticationHandlerFactory(
        IHttpClientFactory httpClientFactory,
        IConnectionRepository connectionRepository,
        IEncryptionService encryptionService)
    {
        _httpClientFactory = httpClientFactory;
        _connectionRepository = connectionRepository;
        _encryptionService = encryptionService;
    }

    public IAuthenticationHandler GetHandler(AuthType authType)
    {
        return authType switch
        {
            AuthType.Basic => new BasicAuthHandler(),
            AuthType.ApiKey => new ApiKeyAuthHandler(),
            AuthType.Bearer => new BearerAuthHandler(),
            AuthType.OAuth2ClientCredentials => new OAuth2ClientCredentialsHandler(_httpClientFactory),
            AuthType.OAuth2RefreshToken => new OAuth2RefreshTokenHandler(_httpClientFactory, _connectionRepository, _encryptionService),
            AuthType.OAuth2AuthCode => new OAuth2AuthCodeHandler(_httpClientFactory, _connectionRepository, _encryptionService),
            AuthType.Custom => new CustomAuthHandler(),
            _ => new NoAuthHandler()
        };
    }
}
