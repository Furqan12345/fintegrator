using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Services.Auth;

public class BasicAuthHandler : IAuthenticationHandler
{
    public Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        var config = JObject.Parse(configJson);
        var username = config["username"]?.ToString();
        var password = config["password"]?.ToString();
        
        var byteArray = Encoding.ASCII.GetBytes($"{username}:{password}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
        
        return Task.CompletedTask;
    }
}

public class ApiKeyAuthHandler : IAuthenticationHandler
{
    public Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        var config = JObject.Parse(configJson);
        var headerName = config["headerName"]?.ToString() ?? "X-Api-Key";
        var apiKey = config["apiKey"]?.ToString() ?? string.Empty;
        
        request.Headers.Add(headerName, apiKey);
        
        return Task.CompletedTask;
    }
}

public class BearerAuthHandler : IAuthenticationHandler
{
    public Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        var config = JObject.Parse(configJson);
        var token = config["token"]?.ToString();
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        return Task.CompletedTask;
    }
}

public class OAuth2Handler : IAuthenticationHandler
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OAuth2Handler(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        // In a real system, this would exchange refresh tokens, handle expiration, etc.
        var config = JObject.Parse(configJson);
        var token = config["accessToken"]?.ToString();
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

public class OAuth2RefreshTokenHandler : IAuthenticationHandler
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OAuth2RefreshTokenHandler(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        var config = JObject.Parse(configJson);
        var tokenUrl = config["tokenUrl"]?.ToString();
        var refreshToken = config["refreshToken"]?.ToString();
        var accessToken = config["accessToken"]?.ToString();

        // If we have a valid access token, use it directly
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            InjectAccessTokenAsQueryParam(request, accessToken);
            return;
        }

        // Otherwise, exchange refresh token for access token
        if (string.IsNullOrWhiteSpace(tokenUrl) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("TokenUrl and RefreshToken are required for OAuth2 Refresh Token flow");
        }

        var client = _httpClientFactory.CreateClient();
        
        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
        tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        
        var bodyContent = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", refreshToken }
        };
        
        tokenRequest.Content = new FormUrlEncodedContent(bodyContent);
        tokenRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        var tokenResponse = await client.SendAsync(tokenRequest);
        var responseContent = await tokenResponse.Content.ReadAsStringAsync();

        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to exchange refresh token: {tokenResponse.StatusCode} - {responseContent}");
        }

        var responseJson = JObject.Parse(responseContent);
        var newAccessToken = responseJson["access_token"]?.ToString();

        if (string.IsNullOrWhiteSpace(newAccessToken))
        {
            throw new InvalidOperationException("Token response did not contain access_token");
        }

        InjectAccessTokenAsQueryParam(request, newAccessToken);
    }

    private void InjectAccessTokenAsQueryParam(HttpRequestMessage request, string accessToken)
    {
        var uri = request.RequestUri;
        if (uri == null) return;
        
        var queryString = string.Empty;
        
        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            queryString = uri.Query.TrimStart('?');
            queryString += "&";
        }
        
        queryString += $"access_token={Uri.EscapeDataString(accessToken)}";
        
        var newUri = new UriBuilder(uri.Scheme, uri.Host, uri.Port, uri.AbsolutePath, queryString);
        request.RequestUri = newUri.Uri;
    }
}

public class NoAuthHandler : IAuthenticationHandler
{
    public Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        return Task.CompletedTask;
    }
}

public class AuthenticationHandlerFactory : IAuthenticationHandlerFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AuthenticationHandlerFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IAuthenticationHandler GetHandler(AuthType authType)
    {
        return authType switch
        {
            AuthType.Basic => new BasicAuthHandler(),
            AuthType.ApiKey => new ApiKeyAuthHandler(),
            AuthType.Bearer => new BearerAuthHandler(),
            AuthType.OAuth2ClientCredentials => new OAuth2Handler(_httpClientFactory),
            AuthType.OAuth2RefreshToken => new OAuth2RefreshTokenHandler(_httpClientFactory),
            _ => new NoAuthHandler()
        };
    }
}
