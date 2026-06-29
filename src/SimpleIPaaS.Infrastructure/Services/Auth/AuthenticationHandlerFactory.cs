using System;
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
    public Task AuthenticateAsync(HttpRequestMessage request, string configJson)
    {
        // In a real system, this would exchange refresh tokens, handle expiration, etc.
        var config = JObject.Parse(configJson);
        var token = config["accessToken"]?.ToString();
        
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        return Task.CompletedTask;
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
    public IAuthenticationHandler GetHandler(AuthType authType)
    {
        return authType switch
        {
            AuthType.Basic => new BasicAuthHandler(),
            AuthType.ApiKey => new ApiKeyAuthHandler(),
            AuthType.Bearer => new BearerAuthHandler(),
            AuthType.OAuth2ClientCredentials => new OAuth2Handler(),
            _ => new NoAuthHandler()
        };
    }
}
