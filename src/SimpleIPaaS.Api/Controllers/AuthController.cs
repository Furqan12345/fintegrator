using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Net.Codecrete.QrCodeGenerator;
using SimpleIPaaS.Api.Security;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Security;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static readonly string DummyPasswordHash =
        new PasswordHasher<AppUser>().HashPassword(new AppUser(), "not-a-real-password-9F!");

    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly TokenService _tokens;
    private readonly AuthAuditService _audit;
    private readonly AuthLinkBuilder _links;
    private readonly IEmailSender _email;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        TokenService tokens,
        AuthAuditService audit,
        AuthLinkBuilder links,
        IEmailSender email,
        ILogger<AuthController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _tokens = tokens;
        _audit = audit;
        _links = links;
        _email = email;
        _logger = logger;
    }

    [HttpGet("setup-status")]
    [AllowAnonymous]
    public async Task<IActionResult> SetupStatus()
    {
        var required = !await _userManager.Users.AnyAsync(HttpContext.RequestAborted);
        return Ok(new SetupStatusDto { Required = required });
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup([FromBody] SetupRequest request)
    {
        await SetupLock.WaitAsync(HttpContext.RequestAborted);
        try
        {
            if (await _userManager.Users.AnyAsync(HttpContext.RequestAborted))
            {
                return Conflict(new { message = "Setup has already been completed." });
            }

            var email = request.Email.Trim();
            var user = new AppUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = request.FullName.Trim(),
                Role = AppRoles.Admin,
                CreatedAt = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                return IdentityProblem(result);
            }

            await AuditAsync("SetupCompleted", true, user, email);
            return Ok(new SetupResponse
            {
                SetupToken = _tokens.CreateSetupToken(user.Id),
                User = AuthDtoMapper.ToDto(user)
            });
        }
        finally
        {
            SetupLock.Release();
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var email = request.Email.Trim();
        var user = await _userManager.FindByEmailAsync(email);

        Microsoft.AspNetCore.Identity.SignInResult? passwordResult = null;
        if (user == null)
        {
            // Keep unknown-email requests computationally similar to known accounts.
            _userManager.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyPasswordHash, request.Password);
        }
        else
        {
            passwordResult = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        }

        if (user == null || user.IsDisabled)
        {
            await AuditAsync("LoginFailed", false, user, email);
            return Unauthorized(GenericLoginError());
        }

        if (passwordResult == null || passwordResult.IsLockedOut)
        {
            await AuditAsync("LoginLockedOut", false, user, email);
            return StatusCode(StatusCodes.Status423Locked, new
            {
                message = "Too many failed sign-in attempts. Try again in a few minutes."
            });
        }

        if (!passwordResult.Succeeded)
        {
            await AuditAsync("LoginFailed", false, user, email);
            return Unauthorized(GenericLoginError());
        }

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.Code) && string.IsNullOrWhiteSpace(request.RecoveryCode))
            {
                return Ok(new LoginResponse { RequiresTwoFactor = true, User = AuthDtoMapper.ToDto(user) });
            }

            var twoFactorOk = false;
            if (!string.IsNullOrWhiteSpace(request.Code))
            {
                twoFactorOk = await _userManager.VerifyTwoFactorTokenAsync(
                    user,
                    TokenOptions.DefaultAuthenticatorProvider,
                    NormalizeTotp(request.Code));
            }

            if (!twoFactorOk && !string.IsNullOrWhiteSpace(request.RecoveryCode))
            {
                var recovery = await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, request.RecoveryCode.Trim());
                twoFactorOk = recovery.Succeeded;
            }

            if (!twoFactorOk)
            {
                await _userManager.AccessFailedAsync(user);
                await AuditAsync("TwoFactorFailed", false, user, email);
                return Unauthorized(GenericLoginError());
            }

            await _userManager.ResetAccessFailedCountAsync(user);
            await AuditAsync("TwoFactorSucceeded", true, user, email);
        }
        else
        {
            await _userManager.ResetAccessFailedCountAsync(user);
            return Ok(new LoginResponse
            {
                RequiresTwoFactorSetup = true,
                SetupToken = _tokens.CreateSetupToken(user.Id),
                User = AuthDtoMapper.ToDto(user)
            });
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        var tokens = await _tokens.IssueAsync(user, GetIp(), GetUserAgent(), HttpContext.RequestAborted);
        await AuditAsync("LoginSucceeded", true, user, email);
        return Ok(ToLoginResponse(tokens, user));
    }

    /// <summary>
    /// Temporary development convenience: signs in as the first enabled admin without a password or 2FA.
    /// Only active when the host is Development AND Auth:DevAutoLogin is true; otherwise it returns 404.
    /// </summary>
    [HttpPost("dev-login")]
    [AllowAnonymous]
    public async Task<IActionResult> DevLogin(
        [FromServices] IWebHostEnvironment environment,
        [FromServices] IConfiguration configuration)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("Auth:DevAutoLogin"))
        {
            return NotFound();
        }

        var user = await _userManager.Users
            .Where(u => u.Role == AppRoles.Admin && !u.IsDisabled)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (user == null)
        {
            return NotFound();
        }

        var tokens = await _tokens.IssueAsync(user, GetIp(), GetUserAgent(), HttpContext.RequestAborted);
        await AuditAsync("DevAutoLogin", true, user, user.Email);
        return Ok(ToLoginResponse(tokens, user));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        var result = await _tokens.RotateAsync(request.RefreshToken, GetIp(), GetUserAgent(), HttpContext.RequestAborted);
        if (result.Failure != RefreshRotationFailure.None || result.User == null || result.Tokens == null)
        {
            if (result.Failure == RefreshRotationFailure.ReuseDetected)
            {
                if (result.UserId is Guid reusedUserId)
                {
                    var reusedUser = await _userManager.FindByIdAsync(reusedUserId.ToString());
                    if (reusedUser != null)
                    {
                        // Changing the security stamp rejects access tokens issued before the
                        // reuse was detected, not just the refresh tokens.
                        await _userManager.UpdateSecurityStampAsync(reusedUser);
                    }
                }

                await AuditAsync("RefreshReuseDetected", false, null, null, "All sessions for the user were revoked.");
            }

            return Unauthorized(GenericLoginError());
        }

        if (await _userManager.IsLockedOutAsync(result.User))
        {
            await _tokens.RevokeAllForUserAsync(result.User.Id, HttpContext.RequestAborted);
            return Unauthorized(GenericLoginError());
        }

        await AuditAsync("TokenRefreshed", true, result.User, result.User.Email);
        return Ok(new RefreshResponse
        {
            AccessToken = result.Tokens.AccessToken,
            RefreshToken = result.Tokens.RefreshToken,
            ExpiresIn = SecondsUntil(result.Tokens.AccessTokenExpiresAt),
            User = AuthDtoMapper.ToDto(result.User)
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request)
    {
        var userId = GetUserId();
        await _tokens.RevokeAsync(request?.RefreshToken, userId, HttpContext.RequestAborted);
        await AuditAsync("Logout", true, null, User.FindFirst(ClaimTypes.Email)?.Value);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await _userManager.GetUserAsync(User);
        return user == null ? Unauthorized() : Ok(AuthDtoMapper.ToDto(user));
    }

    [HttpPost("2fa/setup")]
    [AllowAnonymous]
    public async Task<IActionResult> SetupTwoFactor()
    {
        var userId = _tokens.ValidateSetupToken(Request.Headers["X-Setup-Token"].FirstOrDefault());
        if (userId == null)
        {
            return Unauthorized(new { message = "The setup session expired. Sign in again." });
        }

        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user == null || user.IsDisabled)
        {
            return Unauthorized(GenericLoginError());
        }

        if (user.TwoFactorEnabled)
        {
            return BadRequest(new { message = "Two-factor authentication is already enabled." });
        }

        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            key = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return Problem("Could not create an authenticator key.");
        }

        const string issuer = "fusetor.com";
        var account = user.Email ?? user.UserName ?? "account";
        var authenticatorUri =
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
        var qrCode = QrCode.EncodeText(authenticatorUri, QrCode.Ecc.Medium);
        var svg = qrCode.ToSvgString(4);
        var qrCodeDataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        return Ok(new TwoFactorSetupResponse
        {
            SharedKey = key,
            AuthenticatorUri = authenticatorUri,
            QrCodeDataUri = qrCodeDataUri
        });
    }

    [HttpPost("2fa/enable")]
    [AllowAnonymous]
    public async Task<IActionResult> EnableTwoFactor([FromBody] TwoFactorEnableRequest request)
    {
        var userId = _tokens.ValidateSetupToken(Request.Headers["X-Setup-Token"].FirstOrDefault());
        if (userId == null)
        {
            return Unauthorized(new { message = "The setup session expired. Sign in again." });
        }

        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user == null || user.IsDisabled)
        {
            return Unauthorized(GenericLoginError());
        }

        if (user.TwoFactorEnabled)
        {
            return BadRequest(new { message = "Two-factor authentication is already enabled." });
        }

        var code = NormalizeTotp(request.Code);
        if (!await _userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code))
        {
            await AuditAsync("TwoFactorSetupFailed", false, user, user.Email);
            return BadRequest(new { message = "That code is not valid. Check the authenticator and try again." });
        }

        var enable = await _userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enable.Succeeded)
        {
            return IdentityProblem(enable);
        }

        var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10)
            ?? Enumerable.Empty<string>();
        await _userManager.UpdateSecurityStampAsync(user);

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        var tokens = await _tokens.IssueAsync(user, GetIp(), GetUserAgent(), HttpContext.RequestAborted);
        await AuditAsync("TwoFactorEnabled", true, user, user.Email);
        return Ok(new TwoFactorEnableResponse
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            ExpiresIn = SecondsUntil(tokens.AccessTokenExpiresAt),
            User = AuthDtoMapper.ToDto(user),
            RecoveryCodes = recoveryCodes.ToList()
        });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var email = request.Email.Trim();
        var user = await _userManager.FindByEmailAsync(email);
        if (user != null && !user.IsDisabled)
        {
            try
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var link = _links.BuildResetLink(Request, user.Email ?? email, token, invite: false);
                await _email.SendAsync(
                    user.Email ?? email,
                    "Reset your Fusetor password",
                    $"<p>Use this link to reset your Fusetor password:</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Reset password</a></p><p>This link expires in one hour.</p>",
                    $"Reset your Fusetor password: {link}\nThis link expires in one hour.",
                    HttpContext.RequestAborted);
                await AuditAsync("PasswordResetRequested", true, user, email);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not send a password reset email.");
            }
        }

        return Ok(new { message = "If the account exists, a reset email has been sent." });
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim());
        if (user == null || user.IsDisabled)
        {
            return BadRequest(new { message = "The reset link is invalid or has expired." });
        }

        var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await _tokens.RevokeAllForUserAsync(user.Id, HttpContext.RequestAborted);
        await AuditAsync("PasswordResetCompleted", true, user, user.Email);
        return Ok(new { message = "Password updated. Sign in with your new password." });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return IdentityProblem(result);
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await _tokens.RevokeAllForUserAsync(user.Id, HttpContext.RequestAborted);
        await AuditAsync("PasswordChanged", true, user, user.Email);
        return Ok(new { message = "Password changed. Sign in again on other devices." });
    }

    private LoginResponse ToLoginResponse(IssuedTokens tokens, AppUser user) => new()
    {
        AccessToken = tokens.AccessToken,
        RefreshToken = tokens.RefreshToken,
        ExpiresIn = SecondsUntil(tokens.AccessTokenExpiresAt),
        User = AuthDtoMapper.ToDto(user)
    };

    private static int SecondsUntil(DateTime expiresAt) =>
        Math.Max(1, (int)(expiresAt - DateTime.UtcNow).TotalSeconds);

    private static string NormalizeTotp(string code) =>
        new(code.Where(char.IsLetterOrDigit).ToArray());

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId) ? userId : null;

    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? GetUserAgent() => Request.Headers.UserAgent.ToString();

    private Task AuditAsync(string eventType, bool success, AppUser? user, string? email, string? details = null) =>
        _audit.RecordAsync(eventType, success, user?.Id, email ?? user?.Email, GetIp(), GetUserAgent(), details, HttpContext.RequestAborted);

    private static object GenericLoginError() => new { message = "Invalid email, password, or verification code." };

    private IActionResult IdentityProblem(IdentityResult result) =>
        ValidationProblem(new ValidationProblemDetails(result.Errors
            .GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray())));
}
