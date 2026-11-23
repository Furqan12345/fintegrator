using Microsoft.JSInterop;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Services;

public class AuthSession
{
    private const string RefreshTokenStorageKey = "fusetor.auth.refresh";
    private readonly AuthApiClient _api;
    private readonly IJSRuntime _js;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;
    private string? _refreshToken;

    public AuthSession(AuthApiClient api, IJSRuntime js)
    {
        _api = api;
        _js = js;
    }

    public event Action? Changed;

    public bool IsInitialized { get; private set; }
    public UserDto? User { get; private set; }
    public string? AccessToken => _accessToken;
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_accessToken);
    public bool IsAdmin => string.Equals(User?.Role, "Admin", StringComparison.Ordinal);

    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        try
        {
            _refreshToken = await _js.InvokeAsync<string?>("localStorage.getItem", RefreshTokenStorageKey);
            if (!string.IsNullOrWhiteSpace(_refreshToken))
            {
                await RefreshAsync();
            }

            if (!IsAuthenticated)
            {
                await TryDevLoginAsync();
            }
        }
        catch (Exception)
        {
            // A missing browser storage or an unavailable API simply leaves the user signed out.
        }
        finally
        {
            IsInitialized = true;
            NotifyChanged();
        }
    }

    /// <summary>Signs in automatically when the API has Development auto-login turned on.</summary>
    public async Task<bool> TryDevLoginAsync()
    {
        try
        {
            var response = await _api.DevLoginAsync();
            if (response != null
                && !string.IsNullOrWhiteSpace(response.AccessToken)
                && !string.IsNullOrWhiteSpace(response.RefreshToken))
            {
                await ApplyTokensAsync(response.AccessToken, response.RefreshToken, response.ExpiresIn, response.User);
                return true;
            }
        }
        catch (Exception)
        {
            // Auto-login is optional; fall back to the normal sign-in page.
        }

        return false;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _api.LoginAsync(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.AccessToken) && !string.IsNullOrWhiteSpace(response.RefreshToken))
        {
            await ApplyTokensAsync(response.AccessToken, response.RefreshToken, response.ExpiresIn, response.User);
        }

        return response;
    }

    public async Task<TwoFactorEnableResponse> EnableTwoFactorAsync(string setupToken, string code, CancellationToken cancellationToken = default)
    {
        var response = await _api.EnableTwoFactorAsync(setupToken, code, cancellationToken);
        await ApplyTokensAsync(response.AccessToken, response.RefreshToken, response.ExpiresIn, response.User);
        return response;
    }

    public async Task<bool> RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            return false;
        }

        await _refreshLock.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(_refreshToken))
            {
                return false;
            }

            var response = await _api.RefreshAsync(_refreshToken);
            await ApplyTokensAsync(response.AccessToken, response.RefreshToken, response.ExpiresIn, response.User);
            return true;
        }
        catch (Exception)
        {
            await ClearLocalSessionAsync();
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            if (string.IsNullOrWhiteSpace(_refreshToken) || !await RefreshAsync())
            {
                return null;
            }
        }
        else if (_accessTokenExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
        {
            if (!await RefreshAsync())
            {
                return null;
            }
        }

        return _accessToken;
    }

    public async Task SignOutAsync()
    {
        var accessToken = _accessToken;
        var refreshToken = _refreshToken;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            try
            {
                await _api.LogoutAsync(accessToken, refreshToken);
            }
            catch (Exception)
            {
                // The local session still has to be cleared.
            }
        }

        await ClearLocalSessionAsync();
    }

    private async Task ApplyTokensAsync(string accessToken, string refreshToken, int expiresIn, UserDto? user)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, expiresIn));
        User = user;
        await _js.InvokeVoidAsync("localStorage.setItem", RefreshTokenStorageKey, refreshToken);
        NotifyChanged();
    }

    private async Task ClearLocalSessionAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        _accessTokenExpiresAt = default;
        User = null;
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", RefreshTokenStorageKey);
        }
        catch (Exception)
        {
        }

        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
}
