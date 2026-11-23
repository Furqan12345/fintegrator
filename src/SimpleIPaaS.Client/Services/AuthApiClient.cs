using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Services;

public class AuthApiException : Exception
{
    public AuthApiException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

public class AuthApiClient
{
    private readonly HttpClient _http;

    public AuthApiClient(Uri apiBaseUri)
    {
        _http = new HttpClient
        {
            BaseAddress = apiBaseUri,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<SetupStatusDto> GetSetupStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/auth/setup-status", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<SetupStatusDto>(cancellationToken: cancellationToken)
            ?? new SetupStatusDto();
    }

    public async Task<SetupResponse> SetupAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/setup", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<SetupResponse>(cancellationToken: cancellationToken)
            ?? throw new AuthApiException("The setup response was empty.", response.StatusCode);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/login", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken)
            ?? throw new AuthApiException("The sign-in response was empty.", response.StatusCode);
    }

    /// <summary>Development-only auto sign-in. Returns null when the API has it turned off.</summary>
    public async Task<LoginResponse?> DevLoginAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("api/auth/dev-login", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
    }

    public async Task<RefreshResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/refresh", new RefreshRequest
        {
            RefreshToken = refreshToken
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<RefreshResponse>(cancellationToken: cancellationToken)
            ?? throw new AuthApiException("The refresh response was empty.", response.StatusCode);
    }

    public async Task LogoutAsync(string? accessToken, string refreshToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout")
        {
            Content = JsonContent.Create(new RefreshRequest { RefreshToken = refreshToken })
        };
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        // Signing out locally must succeed even when the server session already expired.
    }

    public async Task<TwoFactorSetupResponse> SetupTwoFactorAsync(string setupToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/2fa/setup");
        request.Headers.Add("X-Setup-Token", setupToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<TwoFactorSetupResponse>(cancellationToken: cancellationToken)
            ?? throw new AuthApiException("The authenticator setup response was empty.", response.StatusCode);
    }

    public async Task<TwoFactorEnableResponse> EnableTwoFactorAsync(string setupToken, string code, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/2fa/enable")
        {
            Content = JsonContent.Create(new TwoFactorEnableRequest { Code = code })
        };
        request.Headers.Add("X-Setup-Token", setupToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<TwoFactorEnableResponse>(cancellationToken: cancellationToken)
            ?? throw new AuthApiException("The two-factor response was empty.", response.StatusCode);
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/forgot-password", new ForgotPasswordRequest
        {
            Email = email
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/reset-password", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response);
        }
    }

    private static async Task<AuthApiException> CreateExceptionAsync(HttpResponseMessage response)
    {
        var message = await ReadErrorAsync(response);
        return new AuthApiException(message, response.StatusCode);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString()!;
                }

                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString()!;
                }

                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    var messages = errors.EnumerateObject()
                        .SelectMany(property => property.Value.ValueKind == JsonValueKind.Array
                            ? property.Value.EnumerateArray().Select(item => item.GetString())
                            : new[] { property.Value.GetString() })
                        .Where(item => !string.IsNullOrWhiteSpace(item));
                    var joined = string.Join(" ", messages);
                    if (!string.IsNullOrWhiteSpace(joined))
                    {
                        return joined;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return response.StatusCode == HttpStatusCode.TooManyRequests
            ? "Too many attempts. Wait a minute and try again."
            : "The request could not be completed.";
    }
}
