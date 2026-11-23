namespace SimpleIPaaS.Infrastructure.Security;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string Viewer = "Viewer";

    public static readonly string[] All = { Admin, User, Viewer };

    public static bool IsValid(string? role) =>
        role is not null && All.Contains(role, StringComparer.OrdinalIgnoreCase);
}
