using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Security;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Security;

public sealed class TokenServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;
    private readonly JwtOptions _jwt;

    public TokenServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(_connection)
            .Options;
        _jwt = new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-audience",
            SigningKey = new string('k', 64),
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30,
            SetupTokenMinutes = 10
        };

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options);

    private static AppUser NewUser() => new()
    {
        Id = Guid.NewGuid(),
        UserName = "admin@example.test",
        Email = "admin@example.test",
        EmailConfirmed = true,
        FullName = "Test Admin",
        Role = AppRoles.Admin,
        SecurityStamp = Guid.NewGuid().ToString("N")
    };

    [Fact]
    public async Task IssueAsync_CreatesAValidAccessTokenAndHashedRefreshToken()
    {
        await using var context = CreateContext();
        var user = NewUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var service = new TokenService(context, _jwt, NullLogger<TokenService>.Instance);

        var tokens = await service.IssueAsync(user, "127.0.0.1", "test-agent");

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Single(context.RefreshTokens);
        Assert.Equal(TokenService.HashRefreshToken(tokens.RefreshToken), context.RefreshTokens.Single().TokenHash);
        Assert.DoesNotContain(tokens.RefreshToken, context.RefreshTokens.Single().TokenHash);

        var principal = new JwtSecurityTokenHandler().ValidateToken(
            tokens.AccessToken,
            TokenService.CreateValidationParameters(_jwt),
            out _);
        Assert.Equal(user.Id.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value);
        Assert.Equal(AppRoles.Admin, principal.FindFirst(ClaimTypes.Role)?.Value);
    }

    [Fact]
    public async Task RotateAsync_RotatesAndDetectsReuse()
    {
        await using var context = CreateContext();
        var user = NewUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var service = new TokenService(context, _jwt, NullLogger<TokenService>.Instance);
        var issued = await service.IssueAsync(user, "127.0.0.1", "test-agent");

        var first = await service.RotateAsync(issued.RefreshToken, "127.0.0.1", "test-agent");
        Assert.Equal(RefreshRotationFailure.None, first.Failure);
        Assert.NotNull(first.Tokens);
        Assert.NotEqual(issued.RefreshToken, first.Tokens!.RefreshToken);

        var reused = await service.RotateAsync(issued.RefreshToken, "127.0.0.1", "test-agent");
        Assert.Equal(RefreshRotationFailure.ReuseDetected, reused.Failure);
        Assert.Equal(user.Id, reused.UserId);

        var afterReuse = await service.RotateAsync(first.Tokens.RefreshToken, "127.0.0.1", "test-agent");
        Assert.Equal(RefreshRotationFailure.Revoked, afterReuse.Failure);
    }

    [Fact]
    public async Task SetupToken_IsShortLivedAndPurposeScoped()
    {
        await using var context = CreateContext();
        var user = NewUser();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var service = new TokenService(context, _jwt, NullLogger<TokenService>.Instance);

        var setupToken = service.CreateSetupToken(user.Id);

        Assert.Equal(user.Id, service.ValidateSetupToken(setupToken));
        Assert.Null(service.ValidateSetupToken(setupToken + "x"));

        var accessToken = (await service.IssueAsync(user, null, null)).AccessToken;
        Assert.Null(service.ValidateSetupToken(accessToken));
    }

    public void Dispose() => _connection.Dispose();
}



