using Microsoft.AspNetCore.Identity;

namespace SimpleIPaaS.Infrastructure.Security;

public class AppUser : IdentityUser<Guid>
{
    public AppUser()
    {
        Id = Guid.NewGuid();
    }

    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = AppRoles.User;
    public bool IsDisabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
