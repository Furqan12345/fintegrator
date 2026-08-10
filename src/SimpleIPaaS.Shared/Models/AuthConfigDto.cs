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

public class OAuth2RefreshTokenConfigDto : AuthConfigDto
{
    public string TokenUrl { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
}

public class AmazonSpApiAuthConfigDto : AuthConfigDto
{
    public string TokenUrl { get; set; } = "https://api.amazon.com/auth/o2/token";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string SessionToken { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string Service { get; set; } = "execute-api";
    public string RoleArn { get; set; } = string.Empty;
    public string RoleSessionName { get; set; } = "fintegrator-sp-api";
    public string ExternalId { get; set; } = string.Empty;
    public string StsRegion { get; set; } = "us-east-1";
    public string StsEndpoint { get; set; } = string.Empty;
    public int DurationSeconds { get; set; } = 3600;
}
