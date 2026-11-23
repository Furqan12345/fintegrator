using SimpleIPaaS.Infrastructure.Security;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Security;

public static class AuthDtoMapper
{
    public static UserDto ToDto(AppUser user) => new()
    {
        Id = user.Id,
        Email = user.Email ?? user.UserName ?? string.Empty,
        FullName = user.FullName,
        Role = user.Role,
        IsDisabled = user.IsDisabled,
        TwoFactorEnabled = user.TwoFactorEnabled,
        CreatedAt = user.CreatedAt,
        LastLoginAt = user.LastLoginAt
    };
}
