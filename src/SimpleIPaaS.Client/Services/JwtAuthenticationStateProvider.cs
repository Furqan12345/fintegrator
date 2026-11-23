using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace SimpleIPaaS.Client.Services;

public class JwtAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly AuthSession _session;

    public JwtAuthenticationStateProvider(AuthSession session)
    {
        _session = session;
        _session.Changed += OnSessionChanged;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = _session.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        }

        try
        {
            var principal = BuildPrincipal(token);
            return Task.FromResult(new AuthenticationState(principal));
        }
        catch (Exception)
        {
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        }
    }

    private void OnSessionChanged() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    public void Dispose() => _session.Changed -= OnSessionChanged;

    private static ClaimsPrincipal BuildPrincipal(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            throw new FormatException("Invalid JWT.");
        }

        var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var document = JsonDocument.Parse(payload);
        var claims = new List<Claim>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var claimType = MapClaimType(property.Name);
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                claims.Add(new Claim(claimType, property.Value.GetString() ?? string.Empty));
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        claims.Add(new Claim(claimType, item.GetString() ?? string.Empty));
                    }
                }
            }
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static string MapClaimType(string name) => name switch
    {
        "sub" => ClaimTypes.NameIdentifier,
        "email" => ClaimTypes.Email,
        "role" => ClaimTypes.Role,
        "name" => ClaimTypes.Name,
        _ => name
    };

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Convert.FromBase64String(padded);
    }
}
