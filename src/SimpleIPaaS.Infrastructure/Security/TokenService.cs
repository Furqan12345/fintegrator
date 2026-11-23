using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Security;

public class TokenService
{
    private readonly IPaaSContext _db;
    private readonly JwtOptions _options;
    private readonly ILogger<TokenService> _logger;

    public TokenService(IPaaSContext db, JwtOptions options, ILogger<TokenService> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public (string Token, DateTime ExpiresAt) CreateAccessToken(AppUser user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? string.Empty : user.FullName),
            new(ClaimTypes.Role, user.Role),
            new("security_stamp", user.SecurityStamp ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: CreateSigningCredentials());

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public string CreateSetupToken(Guid userId)
    {
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("purpose", "2fa_setup"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            },
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_options.SetupTokenMinutes),
            signingCredentials: CreateSigningCredentials());

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public Guid? ValidateSetupToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, CreateValidationParameters(), out _);
            if (!string.Equals(principal.FindFirst("purpose")?.Value, "2fa_setup", StringComparison.Ordinal))
            {
                return null;
            }

            var userIdValue = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(userIdValue, out var userId) ? userId : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public TokenValidationParameters CreateValidationParameters() => CreateValidationParameters(_options);

    public static TokenValidationParameters CreateValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role
    };

    public async Task<IssuedTokens> IssueAsync(AppUser user, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        var (accessToken, expiresAt) = CreateAccessToken(user);
        var (rawRefreshToken, refreshToken) = CreateRefreshToken(user.Id, ipAddress, userAgent);
        _db.RefreshTokens.Add(refreshToken);
        await _db.SaveChangesAsync(cancellationToken);

        return new IssuedTokens
        {
            AccessToken = accessToken,
            AccessTokenExpiresAt = expiresAt,
            RefreshToken = rawRefreshToken
        };
    }

    public async Task<RefreshRotationResult> RotateAsync(
        string? rawRefreshToken,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return new RefreshRotationResult { Failure = RefreshRotationFailure.Invalid };
        }

        var hash = HashRefreshToken(rawRefreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);
        if (stored == null)
        {
            return new RefreshRotationResult { Failure = RefreshRotationFailure.Invalid };
        }

        if (stored.RevokedAt != null)
        {
            if (!string.IsNullOrWhiteSpace(stored.ReplacedByTokenHash))
            {
                await RevokeAllForUserAsync(stored.UserId, cancellationToken);
                _logger.LogWarning("Refresh token reuse detected for user {UserId}; all sessions revoked.", stored.UserId);
                return new RefreshRotationResult
                {
                    Failure = RefreshRotationFailure.ReuseDetected,
                    UserId = stored.UserId
                };
            }

            return new RefreshRotationResult { Failure = RefreshRotationFailure.Revoked };
        }

        if (stored.ExpiresAt <= DateTime.UtcNow)
        {
            return new RefreshRotationResult { Failure = RefreshRotationFailure.Expired };
        }

        var user = await _db.Users.FirstOrDefaultAsync(candidate => candidate.Id == stored.UserId, cancellationToken);
        if (user == null || user.IsDisabled)
        {
            return new RefreshRotationResult { Failure = user == null ? RefreshRotationFailure.Invalid : RefreshRotationFailure.UserDisabled };
        }

        var (accessToken, expiresAt) = CreateAccessToken(user);
        var (newRawRefreshToken, replacement) = CreateRefreshToken(user.Id, ipAddress, userAgent);
        stored.RevokedAt = DateTime.UtcNow;
        stored.ReplacedByTokenHash = replacement.TokenHash;
        _db.RefreshTokens.Add(replacement);
        await _db.SaveChangesAsync(cancellationToken);

        return new RefreshRotationResult
        {
            Failure = RefreshRotationFailure.None,
            User = user,
            Tokens = new IssuedTokens
            {
                AccessToken = accessToken,
                AccessTokenExpiresAt = expiresAt,
                RefreshToken = newRawRefreshToken
            }
        };
    }

    public async Task RevokeAsync(string? rawRefreshToken, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return;
        }

        var hash = HashRefreshToken(rawRefreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(
            token => token.TokenHash == hash && (userId == null || token.UserId == userId),
            cancellationToken);
        if (stored == null || stored.RevokedAt != null)
        {
            return;
        }

        stored.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var activeTokens = await _db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.RevokedAt = now;
        }

        if (activeTokens.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public static string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private (string RawToken, RefreshToken Entity) CreateRefreshToken(Guid userId, string? ipAddress, string? userAgent)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return (raw, new RefreshToken
        {
            UserId = userId,
            TokenHash = HashRefreshToken(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(_options.RefreshTokenDays),
            CreatedAt = DateTime.UtcNow,
            CreatedByIp = ipAddress,
            UserAgent = userAgent
        });
    }

    private SigningCredentials CreateSigningCredentials() =>
        new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)), SecurityAlgorithms.HmacSha256);
}
