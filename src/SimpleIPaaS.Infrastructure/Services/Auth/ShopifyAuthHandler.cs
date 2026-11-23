using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Services.Security;

namespace SimpleIPaaS.Infrastructure.Services.Auth;

// Shopify's client-credentials grant is per shop and returns a short-lived
// Admin API access token. Shopify expects that token in X-Shopify-Access-Token,
// not in Authorization: Bearer, so this cannot use the generic OAuth handler.
public sealed class ShopifyAuthHandler : AuthenticationHandlerBase
{
    internal const string AccessTokenHeader = "X-Shopify-Access-Token";
    private const string ShopifySuffix = ".myshopify.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OutboundUrlPolicy _outboundUrlPolicy;

    public ShopifyAuthHandler(IHttpClientFactory httpClientFactory, OutboundUrlPolicy? outboundUrlPolicy = null)
    {
        _httpClientFactory = httpClientFactory;
        _outboundUrlPolicy = outboundUrlPolicy ?? OutboundPolicyDefaults.Create();
    }

    public override async Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ConfigJson))
        {
            throw new InvalidOperationException(
                "Shopify connection credentials are not configured. Add the shop domain, client ID and client secret to the connection.");
        }

        var config = JObject.Parse(context.ConfigJson);
        var shop = NormalizeShop(config["shop"]?.ToString());
        var clientId = Required(config, "clientId");
        var clientSecret = Required(config, "clientSecret");
        var apiVersion = config["apiVersion"]?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(apiVersion) &&
            !System.Text.RegularExpressions.Regex.IsMatch(apiVersion, @"^\d{4}-\d{2}$"))
        {
            throw new InvalidOperationException("Shopify API version must use the YYYY-MM format, for example 2026-10.");
        }

        var cacheKey = OAuthTokenCache.BuildKey(context.ConnectionId, context.ConfigJson);
        if (context.ForceRefresh)
        {
            OAuthTokenCache.Invalidate(cacheKey);
        }
        else if (OAuthTokenCache.TryGet(cacheKey, out var cachedToken))
        {
            ApplyAccessToken(request, cachedToken);
            return;
        }

        var tokenUrl = $"https://{shop}/admin/oauth/access_token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        };

        var responseJson = await OAuthTokenClient.RequestTokenAsync(
            _httpClientFactory, _outboundUrlPolicy, tokenUrl, form, System.Threading.CancellationToken.None);
        var accessToken = responseJson["access_token"]?.ToString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Shopify token response did not contain access_token.");
        }

        var expiresIn = responseJson["expires_in"]?.ToObject<int?>() ?? 3600;
        OAuthTokenCache.Store(cacheKey, accessToken, expiresIn);
        ApplyAccessToken(request, accessToken);
    }

    private static string NormalizeShop(string? value)
    {
        var shop = (value ?? string.Empty).Trim();
        if (shop.Length == 0)
        {
            throw new InvalidOperationException("Shopify shop domain is required, for example your-store.myshopify.com.");
        }

        if (Uri.TryCreate(shop, UriKind.Absolute, out var uri))
        {
            shop = uri.Host;
        }

        shop = shop.Trim().TrimEnd('/').ToLowerInvariant();
        if (!shop.EndsWith(ShopifySuffix, StringComparison.Ordinal) || shop.Length == ShopifySuffix.Length)
        {
            throw new InvalidOperationException("Shopify shop domain must end with .myshopify.com, for example your-store.myshopify.com.");
        }

        var storeName = shop[..^ShopifySuffix.Length];
        if (storeName.Length == 0 ||
            !storeName.All(character => char.IsLetterOrDigit(character) || character == '-'))
        {
            throw new InvalidOperationException("Shopify shop domain contains invalid characters.");
        }

        return shop;
    }

    private static string Required(JObject config, string key)
    {
        var value = config[key]?.ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Shopify connection requires {key}.")
            : value;
    }

    private static void ApplyAccessToken(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Remove(AccessTokenHeader);
        request.Headers.TryAddWithoutValidation(AccessTokenHeader, accessToken);
        request.Headers.Authorization = null;
    }
}
