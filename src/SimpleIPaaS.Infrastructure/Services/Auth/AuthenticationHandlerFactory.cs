using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
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

public class AmazonSpApiAuthHandler : AuthenticationHandlerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AmazonSpApiAuthHandler(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override async Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        var config = JObject.Parse(context.ConfigJson);
        var credentials = await ResolveCredentialsAsync(config, context);
        var lwaToken = await GetAccessTokenAsync(config, context);
        var effectiveToken = await GetRestrictedDataTokenAsync(request, config, context, credentials, lwaToken);
        SignRequest(request, credentials, config["region"]?.ToString() ?? Required(config, "region"), config["service"]?.ToString() ?? "execute-api", effectiveToken);
    }

    private static readonly ConcurrentDictionary<string, CachedAwsCredentials> AwsCredentialsCache = new();
    private static readonly ConcurrentDictionary<string, CachedRestrictedToken> RestrictedTokenCache = new();

    private sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string SessionToken);
    private sealed record CachedAwsCredentials(AwsCredentials Credentials, DateTimeOffset ExpiresAt);
    private sealed record CachedRestrictedToken(string Token, DateTimeOffset ExpiresAt);

    private async Task<AwsCredentials> ResolveCredentialsAsync(JObject config, AuthenticationContext context)
    {
        var baseCredentials = new AwsCredentials(Required(config, "accessKeyId"), Required(config, "secretAccessKey"), config["sessionToken"]?.ToString() ?? string.Empty);
        var roleArn = config["roleArn"]?.ToString();
        if (string.IsNullOrWhiteSpace(roleArn)) return baseCredentials;

        var cacheKey = $"{context.ConnectionId}:{roleArn}:{config["externalId"]}:{config["stsRegion"]}";
        if (!context.ForceRefresh && AwsCredentialsCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached.Credentials;
        }

        var region = config["stsRegion"]?.ToString() ?? config["region"]?.ToString() ?? "us-east-1";
        var endpoint = config["stsEndpoint"]?.ToString();
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = $"https://sts.{region}.amazonaws.com";
        var form = new Dictionary<string, string>
        {
            ["Action"] = "AssumeRole",
            ["Version"] = "2011-06-15",
            ["RoleArn"] = roleArn,
            ["RoleSessionName"] = config["roleSessionName"]?.ToString() ?? "fintegrator-sp-api",
            ["DurationSeconds"] = Math.Clamp(config["durationSeconds"]?.Value<int>() ?? 3600, 900, 43200).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var externalId = config["externalId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(externalId)) form["ExternalId"] = externalId;

        using var stsRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        await SignRequestAsync(stsRequest, baseCredentials, region, "sts", string.Empty);
        var response = await _httpClientFactory.CreateClient().SendAsync(stsRequest);
        var xml = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"STS AssumeRole failed: {response.StatusCode}");
        var document = XDocument.Parse(xml);
        var temporary = new AwsCredentials(
            RequiredXml(document, "AccessKeyId"),
            RequiredXml(document, "SecretAccessKey"),
            RequiredXml(document, "SessionToken"));
        var expirationText = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Expiration")?.Value;
        var expiration = DateTimeOffset.TryParse(expirationText, out var parsed) ? parsed : DateTimeOffset.UtcNow.AddHours(1);
        AwsCredentialsCache[cacheKey] = new CachedAwsCredentials(temporary, expiration);
        return temporary;
    }

    private async Task<string> GetRestrictedDataTokenAsync(HttpRequestMessage request, JObject config, AuthenticationContext context, AwsCredentials credentials, string lwaToken)
    {
        if (string.IsNullOrWhiteSpace(context.RequestMetadataJson)) return lwaToken;
        var metadata = JObject.Parse(context.RequestMetadataJson);
        if (metadata["amazonSpApi"]?["restrictedData"]?.Value<bool>() != true) return lwaToken;
        var key = $"{context.ConnectionId}:{metadata["amazonSpApi"]}";
        if (!context.ForceRefresh && RestrictedTokenCache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached.Token;

        var resources = metadata["amazonSpApi"]?["restrictedResources"] as JArray ?? throw new InvalidOperationException("RDT requires restrictedResources.");
        using var rdtRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/tokens/2021-03-01/restrictedDataToken"));
        rdtRequest.Content = new StringContent(new JObject { ["restrictedResources"] = resources }.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        rdtRequest.Headers.TryAddWithoutValidation("x-amz-access-token", lwaToken);
        await SignRequestAsync(rdtRequest, credentials, config["region"]?.ToString() ?? "us-east-1", config["service"]?.ToString() ?? "execute-api", lwaToken);
        var response = await _httpClientFactory.CreateClient().SendAsync(rdtRequest);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Amazon restricted data token request failed: {response.StatusCode}");
        var result = JObject.Parse(body);
        var token = result["restrictedDataToken"]?.ToString() ?? throw new InvalidOperationException("RDT response did not contain restrictedDataToken.");
        var expiresIn = result["expiresIn"]?.Value<int>() ?? 3600;
        RestrictedTokenCache[key] = new CachedRestrictedToken(token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        return token;
    }

    private static void SignRequest(HttpRequestMessage request, AwsCredentials credentials, string region, string service, string accessToken)
    {
        SignRequestAsync(request, credentials, region, service, accessToken).GetAwaiter().GetResult();
    }

    private static async Task SignRequestAsync(HttpRequestMessage request, AwsCredentials credentials, string region, string service, string accessToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Amazon requests require an absolute URL.");
        var payloadHash = await GetPayloadHashAsync(request);
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        request.Headers.Remove("Authorization");
        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.Remove("x-amz-access-token");
        request.Headers.Remove("x-amz-security-token");
        request.Headers.Host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        if (!string.IsNullOrWhiteSpace(accessToken)) request.Headers.TryAddWithoutValidation("x-amz-access-token", accessToken);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrWhiteSpace(credentials.SessionToken)) request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = request.Headers.Host, ["x-amz-content-sha256"] = payloadHash, ["x-amz-date"] = amzDate };
        if (!string.IsNullOrWhiteSpace(accessToken)) headers["x-amz-access-token"] = accessToken;
        if (!string.IsNullOrWhiteSpace(credentials.SessionToken)) headers["x-amz-security-token"] = credentials.SessionToken;
        var signedHeaders = string.Join(";", headers.Keys);
        var canonicalRequest = string.Join("\n", request.Method.Method.ToUpperInvariant(), CanonicalUri(uri), CanonicalQuery(uri), string.Join("\n", headers.Select(header => $"{header.Key}:{NormalizeHeaderValue(header.Value)}")) + "\n", signedHeaders, payloadHash);
        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, scope, HashHex(Encoding.UTF8.GetBytes(canonicalRequest)));
        var signature = Convert.ToHexString(Hmac(DeriveSigningKey(credentials.SecretAccessKey, dateStamp, region, service), stringToSign)).ToLowerInvariant();
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static string RequiredXml(XDocument document, string name) => document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value ?? throw new InvalidOperationException($"STS response did not contain {name}.");

    private async Task<string> GetAccessTokenAsync(JObject config, AuthenticationContext context)
    {
        var cacheKey = OAuthTokenCache.BuildKey(context.ConnectionId, context.ConfigJson);
        if (context.ForceRefresh)
        {
            OAuthTokenCache.Invalidate(cacheKey);
        }
        else if (OAuthTokenCache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        var configuredToken = config["accessToken"]?.ToString();
        var refreshToken = config["refreshToken"]?.ToString();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            if (string.IsNullOrWhiteSpace(configuredToken))
            {
                throw new InvalidOperationException("Amazon SP-API requires a refreshToken or accessToken.");
            }

            return configuredToken;
        }

        var tokenUrl = config["tokenUrl"]?.ToString();
        var clientId = config["clientId"]?.ToString();
        var clientSecret = config["clientSecret"]?.ToString();
        if (string.IsNullOrWhiteSpace(tokenUrl) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Amazon SP-API requires tokenUrl, clientId, and clientSecret when using refreshToken.");
        }

        var response = await OAuthTokenClient.RequestTokenAsync(_httpClientFactory, tokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        });
        var accessToken = response["access_token"]?.ToString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Amazon LWA response did not contain access_token.");
        }

        OAuthTokenCache.Store(cacheKey, accessToken, response["expires_in"]?.ToObject<int?>() ?? 3600);
        return accessToken;
    }

    private static string Required(JObject config, string name)
    {
        var value = config[name]?.ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Amazon SP-API requires {name}.")
            : value;
    }

    private static async Task<string> GetPayloadHashAsync(HttpRequestMessage request)
    {
        var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync();
        return HashHex(body);
    }

    private static string CanonicalUri(Uri uri) => string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : string.Join("/", uri.AbsolutePath.Split('/').Select(Uri.EscapeDataString));

    private static string CanonicalQuery(Uri uri) => string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Select(pair => (Name: Uri.EscapeDataString(Uri.UnescapeDataString(pair[0])), Value: Uri.EscapeDataString(Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty))))
        .OrderBy(pair => pair.Name, StringComparer.Ordinal)
        .ThenBy(pair => pair.Value, StringComparer.Ordinal)
        .Select(pair => $"{pair.Name}={pair.Value}"));

    private static string NormalizeHeaderValue(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static byte[] DeriveSigningKey(string secret, string date, string region, string service)
    {
        var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        var regionKey = Hmac(dateKey, region);
        var serviceKey = Hmac(regionKey, service);
        return Hmac(serviceKey, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string value) => Hmac(key, Encoding.UTF8.GetBytes(value));

    private static byte[] Hmac(byte[] key, byte[] value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(value);
    }

    private static string HashHex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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
            AuthType.AmazonSpApi => new AmazonSpApiAuthHandler(_httpClientFactory),
            AuthType.Custom => new CustomAuthHandler(),
            _ => new NoAuthHandler()
        };
    }
}
