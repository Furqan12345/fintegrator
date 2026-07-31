using System.Net;
using System.Text;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Infrastructure.Services.Auth;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Auth;

public class AuthenticationHandlerFactoryTests
{
    private static AuthenticationHandlerFactory CreateFactory(IHttpClientFactory? httpClientFactory = null, StubConnectionRepository? connections = null)
    {
        return new AuthenticationHandlerFactory(
            httpClientFactory ?? StubHttpClientFactory.Unreachable(),
            connections ?? new StubConnectionRepository(),
            new PassthroughEncryptionService());
    }

    private static HttpRequestMessage Request(string url = "https://api.example.test/orders") =>
        new(HttpMethod.Get, url);

    [Fact]
    public async Task BasicHandler_SetsBase64EncodedAuthorizationHeader()
    {
        var handler = CreateFactory().GetHandler(AuthType.Basic);
        using var request = Request();

        await handler.AuthenticateAsync(request, "{\"username\":\"alice\",\"password\":\"p@ss:word\"}");

        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.ASCII.GetBytes("alice:p@ss:word")),
            request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task BearerHandler_SetsBearerAuthorizationHeader()
    {
        var handler = CreateFactory().GetHandler(AuthType.Bearer);
        using var request = Request();

        await handler.AuthenticateAsync(request, "{\"token\":\"static-token-123\"}");

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("static-token-123", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ApiKeyHandler_PlacesTheKeyInTheConfiguredHeader()
    {
        var handler = CreateFactory().GetHandler(AuthType.ApiKey);
        using var request = Request();

        await handler.AuthenticateAsync(request, "{\"headerName\":\"X-Tenant-Key\",\"apiKey\":\"abc123\",\"placement\":\"header\"}");

        Assert.True(request.Headers.TryGetValues("X-Tenant-Key", out var values));
        Assert.Equal("abc123", Assert.Single(values!));
        Assert.Equal("https://api.example.test/orders", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ApiKeyHandler_DefaultsToTheXApiKeyHeader()
    {
        var handler = CreateFactory().GetHandler(AuthType.ApiKey);
        using var request = Request();

        await handler.AuthenticateAsync(request, "{\"apiKey\":\"abc123\"}");

        Assert.True(request.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("abc123", Assert.Single(values!));
    }

    [Fact]
    public async Task ApiKeyHandler_PlacesTheKeyInTheQueryStringWhenConfigured()
    {
        var handler = CreateFactory().GetHandler(AuthType.ApiKey);
        using var request = Request();

        await handler.AuthenticateAsync(request, "{\"headerName\":\"api_key\",\"apiKey\":\"a b&c\",\"placement\":\"query\"}");

        Assert.Empty(request.Headers);
        Assert.Equal("?api_key=a%20b%26c", request.RequestUri!.Query);
    }

    [Fact]
    public async Task CustomHandler_EmitsEveryConfiguredHeader()
    {
        var handler = CreateFactory().GetHandler(AuthType.Custom);
        using var request = Request();

        await handler.AuthenticateAsync(request,
            "{\"headers\":[{\"name\":\"X-One\",\"value\":\"1\"},{\"name\":\"X-Two\",\"value\":\"2\"},{\"name\":\"  \",\"value\":\"ignored\"}]}");

        Assert.Equal("1", Assert.Single(request.Headers.GetValues("X-One")));
        Assert.Equal("2", Assert.Single(request.Headers.GetValues("X-Two")));
        Assert.Equal(2, request.Headers.Count());
    }

    [Fact]
    public async Task NoAuthHandler_LeavesTheRequestUntouched()
    {
        var handler = CreateFactory().GetHandler(AuthType.None);
        using var request = Request();

        await handler.AuthenticateAsync(request, "{}");

        Assert.Null(request.Headers.Authorization);
        Assert.Empty(request.Headers);
        Assert.Equal("https://api.example.test/orders", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ClientCredentialsHandler_ExchangesCredentialsAtTheTokenEndpoint()
    {
        var messageHandler = StubHttpMessageHandler.Json("{\"access_token\":\"issued-token\",\"expires_in\":3600}");
        using var clientFactory = new StubHttpClientFactory(messageHandler);
        var handler = CreateFactory(clientFactory).GetHandler(AuthType.OAuth2ClientCredentials);
        using var request = Request();

        var config = "{\"tokenUrl\":\"https://idp.example.test/token\",\"clientId\":\"cid\",\"clientSecret\":\"csecret\"," +
                     $"\"scope\":\"orders.read\",\"nonce\":\"{Guid.NewGuid()}\"}}";

        await handler.AuthenticateAsync(request, new AuthenticationContext { ConfigJson = config });

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("issued-token", request.Headers.Authorization.Parameter);

        var body = Assert.Single(messageHandler.RequestBodies);
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("client_id=cid", body);
        Assert.Contains("scope=orders.read", body);
    }

    [Fact]
    public async Task ClientCredentialsHandler_ThrowsWhenTheTokenEndpointFails()
    {
        var messageHandler = StubHttpMessageHandler.Json("{\"error\":\"invalid_client\"}", HttpStatusCode.Unauthorized);
        using var clientFactory = new StubHttpClientFactory(messageHandler);
        var handler = CreateFactory(clientFactory).GetHandler(AuthType.OAuth2ClientCredentials);
        using var request = Request();

        var config = $"{{\"tokenUrl\":\"https://idp.example.test/token\",\"clientId\":\"cid\",\"nonce\":\"{Guid.NewGuid()}\"}}";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.AuthenticateAsync(request, new AuthenticationContext { ConfigJson = config }));
    }

    [Fact]
    public async Task ClientCredentialsHandler_UsesAStaticTokenWhenNoTokenUrlIsConfigured()
    {
        var handler = CreateFactory().GetHandler(AuthType.OAuth2ClientCredentials);
        using var request = Request();

        await handler.AuthenticateAsync(request, "{\"accessToken\":\"pre-issued\"}");

        Assert.Equal("pre-issued", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task RefreshTokenHandler_PlacesAStaticAccessTokenInTheQueryStringByDefault()
    {
        var handler = CreateFactory().GetHandler(AuthType.OAuth2RefreshToken);
        using var request = Request();

        await handler.AuthenticateAsync(request,
            $"{{\"accessToken\":\"query-token\",\"nonce\":\"{Guid.NewGuid()}\"}}");

        Assert.Null(request.Headers.Authorization);
        Assert.Equal("?access_token=query-token", request.RequestUri!.Query);
    }

    [Fact]
    public async Task RefreshTokenHandler_UsesTheAuthorizationHeaderWhenPlacementIsHeader()
    {
        var handler = CreateFactory().GetHandler(AuthType.OAuth2RefreshToken);
        using var request = Request();

        await handler.AuthenticateAsync(request,
            $"{{\"accessToken\":\"header-token\",\"placement\":\"header\",\"nonce\":\"{Guid.NewGuid()}\"}}");

        Assert.Equal("header-token", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task RefreshTokenHandler_RefreshesAndPersistsARotatedRefreshToken()
    {
        var messageHandler = StubHttpMessageHandler.Json(
            "{\"access_token\":\"new-access\",\"refresh_token\":\"rotated-refresh\",\"expires_in\":3600}");
        using var clientFactory = new StubHttpClientFactory(messageHandler);

        var connection = new SimpleIPaaS.Domain.Entities.Connection { AuthType = AuthType.OAuth2RefreshToken };
        var connections = new StubConnectionRepository();
        connections.Seed(connection);

        var handler = CreateFactory(clientFactory, connections).GetHandler(AuthType.OAuth2RefreshToken);
        using var request = Request();

        var config = "{\"tokenUrl\":\"https://idp.example.test/token\",\"refreshToken\":\"original-refresh\"," +
                     $"\"placement\":\"header\",\"nonce\":\"{Guid.NewGuid()}\"}}";

        await handler.AuthenticateAsync(request, new AuthenticationContext
        {
            ConfigJson = config,
            ConnectionId = connection.Id
        });

        Assert.Equal("new-access", request.Headers.Authorization!.Parameter);
        Assert.Contains("grant_type=refresh_token", Assert.Single(messageHandler.RequestBodies));

        var updated = Assert.Single(connections.Updated);
        Assert.Contains("rotated-refresh", updated.AuthConfigJson);
    }

    [Fact]
    public async Task AuthCodeHandler_UsesTheAuthorizationHeaderWithoutExplicitPlacement()
    {
        var handler = CreateFactory().GetHandler(AuthType.OAuth2AuthCode);
        using var request = Request();

        await handler.AuthenticateAsync(request, $"{{\"accessToken\":\"auth-code-token\",\"nonce\":\"{Guid.NewGuid()}\"}}");

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("auth-code-token", request.Headers.Authorization.Parameter);
        Assert.Equal(string.Empty, request.RequestUri!.Query);
    }

    [Fact]
    public void GetHandler_ReturnsAHandlerForEveryAuthType()
    {
        var factory = CreateFactory();

        foreach (var authType in Enum.GetValues<AuthType>())
        {
            Assert.NotNull(factory.GetHandler(authType));
        }
    }

    [Fact]
    public void GetHandler_OnlyFallsBackToNoAuthForAuthTypeNone()
    {
        var factory = CreateFactory();

        foreach (var authType in Enum.GetValues<AuthType>())
        {
            var handler = factory.GetHandler(authType);

            if (authType == AuthType.None)
            {
                Assert.IsType<NoAuthHandler>(handler);
            }
            else
            {
                Assert.False(handler is NoAuthHandler,
                    $"AuthType.{authType} silently falls through to NoAuthHandler, which would send unauthenticated requests.");
            }
        }
    }

    [Fact]
    public void GetHandler_ReturnsADistinctHandlerTypePerAuthType()
    {
        var factory = CreateFactory();

        var handlerTypes = Enum.GetValues<AuthType>()
            .ToDictionary(authType => authType, authType => factory.GetHandler(authType).GetType());

        Assert.Equal(typeof(BasicAuthHandler), handlerTypes[AuthType.Basic]);
        Assert.Equal(typeof(BearerAuthHandler), handlerTypes[AuthType.Bearer]);
        Assert.Equal(typeof(ApiKeyAuthHandler), handlerTypes[AuthType.ApiKey]);
        Assert.Equal(typeof(OAuth2ClientCredentialsHandler), handlerTypes[AuthType.OAuth2ClientCredentials]);
        Assert.Equal(typeof(OAuth2RefreshTokenHandler), handlerTypes[AuthType.OAuth2RefreshToken]);
        Assert.Equal(typeof(OAuth2AuthCodeHandler), handlerTypes[AuthType.OAuth2AuthCode]);
        Assert.Equal(typeof(CustomAuthHandler), handlerTypes[AuthType.Custom]);
        Assert.Equal(typeof(NoAuthHandler), handlerTypes[AuthType.None]);
    }
}
