using System.ComponentModel.DataAnnotations;

namespace SimpleIPaaS.Shared.Models;

public class UserDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsDisabled { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class SetupStatusDto
{
    public bool Required { get; set; }
}

public class SetupRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required, MinLength(12), MaxLength(256)]
    public string Password { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string FullName { get; set; } = string.Empty;
}

public class SetupResponse
{
    public string SetupToken { get; set; } = string.Empty;
    public UserDto User { get; set; } = new();
}

public class LoginRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? Code { get; set; }

    [MaxLength(64)]
    public string? RecoveryCode { get; set; }
}

public class LoginResponse
{
    public bool RequiresTwoFactor { get; set; }
    public bool RequiresTwoFactorSetup { get; set; }
    public string? SetupToken { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public UserDto? User { get; set; }
}

public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}

public class RefreshResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public UserDto User { get; set; } = new();
}

public class TwoFactorSetupResponse
{
    public string SharedKey { get; set; } = string.Empty;
    public string AuthenticatorUri { get; set; } = string.Empty;
    public string QrCodeDataUri { get; set; } = string.Empty;
}

public class TwoFactorEnableRequest
{
    [Required, MaxLength(32)]
    public string Code { get; set; } = string.Empty;
}

public class TwoFactorEnableResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public UserDto User { get; set; } = new();
    public IReadOnlyList<string> RecoveryCodes { get; set; } = Array.Empty<string>();
}

public class ForgotPasswordRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;

    [Required, MinLength(12), MaxLength(256)]
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, MinLength(12), MaxLength(256)]
    public string NewPassword { get; set; } = string.Empty;
}

public class InviteUserRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = "User";
}

public class InviteUserResponse
{
    public Guid UserId { get; set; }
    public string InviteUrl { get; set; } = string.Empty;
    public bool EmailSent { get; set; }
}

public class UpdateRoleRequest
{
    [Required]
    public string Role { get; set; } = string.Empty;
}

public class AuthAuditDto
{
    public DateTime TimestampUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? Details { get; set; }
}
