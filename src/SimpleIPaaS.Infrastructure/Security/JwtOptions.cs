namespace SimpleIPaaS.Infrastructure.Security;

public class JwtOptions
{
    public string Issuer { get; set; } = "fusetor";
    public string Audience { get; set; } = "fusetor";
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 7 * 24 * 60;
    public int RefreshTokenDays { get; set; } = 30;
    public int SetupTokenMinutes { get; set; } = 10;
}
