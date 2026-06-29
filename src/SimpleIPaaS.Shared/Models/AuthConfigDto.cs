using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public abstract class AuthConfigDto
{
    // Base class for auth configs
}

public class BasicAuthConfigDto : AuthConfigDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ApiKeyAuthConfigDto : AuthConfigDto
{
    public string HeaderName { get; set; } = "x-api-key";
    public string ApiKey { get; set; } = string.Empty;
}

public class BearerTokenAuthConfigDto : AuthConfigDto
{
    public string Token { get; set; } = string.Empty;
}

public class OAuth2ConfigDto : AuthConfigDto
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string AuthUrl { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}
