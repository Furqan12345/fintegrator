using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Validation;

// Server-side rules for connection saves. The wizard mirrors them for immediate feedback,
// but the API is authoritative: a connection with no name or no usable base URL is never
// persisted (it used to be possible to save a blank row from the wizard).
public static class ConnectionValidation
{
    public const int MaxNameLength = 200;
    public const int MaxBaseUrlLength = 2000;

    public static List<string> Validate(ConnectionDto dto, bool isCreate)
    {
        var errors = new List<string>();

        var name = (dto.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors.Add("A connection name is required.");
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add($"The connection name must be {MaxNameLength} characters or fewer.");
        }

        var baseUrl = (dto.BaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length == 0)
        {
            errors.Add("A base URL is required.");
        }
        else if (baseUrl.Length > MaxBaseUrlLength)
        {
            errors.Add($"The base URL must be {MaxBaseUrlLength} characters or fewer.");
        }
        else if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("The base URL must be an absolute http(s) URL, for example https://api.example.com.");
        }

        if (RequiredAuthFields(dto.AuthType) is { } authRule)
        {
            var configJson = dto.AuthConfigJson ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configJson))
            {
                // On update an empty auth config means "keep the stored credentials", which is
                // exactly what the wizard sends when the secret fields were left blank.
                if (isCreate)
                {
                    errors.Add($"Credentials are required for {authRule.Label} connections. Fill them in, or choose None as the auth type.");
                }
            }
            else
            {
                var missing = MissingFields(configJson, authRule.Fields);
                if (missing.Count > 0)
                {
                    errors.Add($"The {authRule.Label} credentials are incomplete: {string.Join(", ", missing)} required.");
                }

                if (string.Equals(dto.AuthType, "Shopify", StringComparison.OrdinalIgnoreCase))
                {
                    errors.AddRange(ValidateShopifyConfig(configJson));
                }
            }
        }

        return errors;
    }

    private static List<string> MissingFields(string authConfigJson, IReadOnlyList<(string Key, string Label)> fields)
    {
        var missing = new List<string>();
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(authConfigJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new List<string> { "the auth configuration is not valid JSON" };
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new List<string> { "the auth configuration must be a JSON object" };
        }

        foreach (var (key, label) in fields)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind switch
                {
                    JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
                    JsonValueKind.Array => value.GetArrayLength() == 0,
                    JsonValueKind.Null or JsonValueKind.Undefined => true,
                    _ => false
                })
            {
                missing.Add(label);
            }
        }

        return missing;
    }

    private static List<string> ValidateShopifyConfig(string authConfigJson)
    {
        var errors = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(authConfigJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return errors;

            var shop = root.TryGetProperty("shop", out var shopElement) ? shopElement.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (shop.Length > 0)
            {
                if (Uri.TryCreate(shop, UriKind.Absolute, out var uri)) shop = uri.Host;
                shop = shop.TrimEnd('/').ToLowerInvariant();
                var storeName = shop.EndsWith(".myshopify.com", StringComparison.Ordinal)
                    ? shop[..^".myshopify.com".Length]
                    : string.Empty;
                if (storeName.Length == 0 || storeName.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
                {
                    errors.Add("The Shopify shop domain must be a valid your-store.myshopify.com domain.");
                }
            }

            var apiVersion = root.TryGetProperty("apiVersion", out var versionElement) ? versionElement.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (apiVersion.Length > 0 &&
                !System.Text.RegularExpressions.Regex.IsMatch(apiVersion, @"^\d{4}-\d{2}$"))
            {
                errors.Add("The Shopify API version must use the YYYY-MM format, for example 2026-10.");
            }
        }
        catch (JsonException)
        {
            // MissingFields already reports malformed JSON.
        }

        return errors;
    }

    private static (string Label, IReadOnlyList<(string Key, string Label)> Fields)? RequiredAuthFields(string? authType) =>
        authType switch
        {
            "Basic" => ("basic authentication", new[] { ("username", "username"), ("password", "password") }),
            "Bearer" => ("bearer token", new[] { ("token", "token") }),
            "ApiKey" => ("API key", new[] { ("headerName", "header name"), ("apiKey", "API key") }),
            "OAuth2ClientCredentials" => ("OAuth 2.0 client credentials", new[]
                { ("tokenUrl", "token URL"), ("clientId", "client ID"), ("clientSecret", "client secret") }),
            "OAuth2RefreshToken" => ("OAuth 2.0 refresh token", new[] { ("tokenUrl", "token URL"), ("refreshToken", "refresh token") }),
            "OAuth2AuthCode" => ("OAuth 2.0 authorization code", new[]
                { ("tokenUrl", "token URL"), ("clientId", "client ID"), ("clientSecret", "client secret"), ("accessToken", "access token"), ("refreshToken", "refresh token") }),
            "AmazonSpApi" => ("Amazon SP-API", new[]
                { ("clientId", "LWA client ID"), ("clientSecret", "LWA client secret"), ("refreshToken", "LWA refresh token"), ("accessKeyId", "AWS access key ID"), ("secretAccessKey", "AWS secret access key") }),
            "Shopify" => ("Shopify Admin API", new[]
                { ("shop", "shop domain"), ("clientId", "client ID"), ("clientSecret", "client secret") }),
            "Custom" => ("custom header", new[] { ("headers", "at least one header") }),
            _ => null
        };
}
