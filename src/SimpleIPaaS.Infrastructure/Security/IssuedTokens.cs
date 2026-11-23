namespace SimpleIPaaS.Infrastructure.Security;

public sealed class IssuedTokens
{
    public string AccessToken { get; init; } = string.Empty;
    public DateTime AccessTokenExpiresAt { get; init; }
    public string RefreshToken { get; init; } = string.Empty;
}

public enum RefreshRotationFailure
{
    None,
    Invalid,
    Expired,
    Revoked,
    ReuseDetected,
    UserDisabled
}

public sealed class RefreshRotationResult
{
    public RefreshRotationFailure Failure { get; init; }
    public Guid? UserId { get; init; }
    public AppUser? User { get; init; }
    public IssuedTokens? Tokens { get; init; }
}
