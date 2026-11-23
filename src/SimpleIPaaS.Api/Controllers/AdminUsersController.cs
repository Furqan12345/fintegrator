using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Security;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Security;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "Admin")]
public class AdminUsersController : ControllerBase
{
    private readonly UserManager<AppUser> _userManager;
    private readonly TokenService _tokens;
    private readonly AuthAuditService _audit;
    private readonly AuthLinkBuilder _links;
    private readonly IEmailSender _email;
    private readonly ILogger<AdminUsersController> _logger;

    public AdminUsersController(
        UserManager<AppUser> userManager,
        TokenService tokens,
        AuthAuditService audit,
        AuthLinkBuilder links,
        IEmailSender email,
        ILogger<AdminUsersController> logger)
    {
        _userManager = userManager;
        _tokens = tokens;
        _audit = audit;
        _links = links;
        _email = email;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int take = 200)
    {
        var query = _userManager.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(user =>
                (user.Email ?? string.Empty).Contains(term) ||
                user.FullName.Contains(term));
        }

        var users = await query
            .OrderBy(user => user.Email)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(HttpContext.RequestAborted);

        return Ok(users.Select(AuthDtoMapper.ToDto));
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request)
    {
        var role = NormalizeRole(request.Role);
        if (role == null)
        {
            return BadRequest(new { message = "Role must be Admin, User, or Viewer." });
        }

        var email = request.Email.Trim();
        if (await _userManager.FindByEmailAsync(email) != null)
        {
            return Conflict(new { message = "A user with that email already exists." });
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = request.FullName.Trim(),
            Role = role,
            CreatedAt = DateTime.UtcNow
        };

        var admin = await _userManager.GetUserAsync(User);
        var created = await _userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            return IdentityProblem(created);
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var link = _links.BuildResetLink(Request, email, token, invite: true);
        var emailSent = await TrySendInviteAsync(email, link);
        await AuditAsync("UserInvited", true, admin, admin?.Email, $"Invited {email} as {role}.");

        return Ok(new InviteUserResponse
        {
            UserId = user.Id,
            InviteUrl = link,
            EmailSent = emailSent
        });
    }

    [HttpPut("{userId:guid}/role")]
    public async Task<IActionResult> UpdateRole(Guid userId, [FromBody] UpdateRoleRequest request)
    {
        var role = NormalizeRole(request.Role);
        if (role == null)
        {
            return BadRequest(new { message = "Role must be Admin, User, or Viewer." });
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return NotFound();
        }

        if (string.Equals(user.Role, AppRoles.Admin, StringComparison.Ordinal) &&
            !string.Equals(role, AppRoles.Admin, StringComparison.Ordinal) &&
            await CountActiveAdminsAsync() <= 1)
        {
            return BadRequest(new { message = "At least one active Admin is required." });
        }

        user.Role = role;
        user.UpdatedAt = DateTime.UtcNow;
        await _userManager.UpdateSecurityStampAsync(user);
        await _tokens.RevokeAllForUserAsync(user.Id, HttpContext.RequestAborted);
        await AuditAsync("UserRoleChanged", true, await CurrentUserAsync(), User.FindFirst(ClaimTypes.Email)?.Value, $"{user.Email} is now {role}.");
        return Ok(AuthDtoMapper.ToDto(user));
    }

    [HttpPost("{userId:guid}/disable")]
    public async Task<IActionResult> Disable(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return NotFound();
        }

        var currentUserId = GetUserId();
        if (currentUserId == user.Id)
        {
            return BadRequest(new { message = "You cannot disable your own account." });
        }

        if (!user.IsDisabled &&
            string.Equals(user.Role, AppRoles.Admin, StringComparison.Ordinal) &&
            await CountActiveAdminsAsync() <= 1)
        {
            return BadRequest(new { message = "At least one active Admin is required." });
        }

        user.IsDisabled = true;
        user.UpdatedAt = DateTime.UtcNow;
        await _userManager.UpdateSecurityStampAsync(user);
        await _tokens.RevokeAllForUserAsync(user.Id, HttpContext.RequestAborted);
        await AuditAsync("UserDisabled", true, await CurrentUserAsync(), User.FindFirst(ClaimTypes.Email)?.Value, $"Disabled {user.Email}.");
        return NoContent();
    }

    [HttpPost("{userId:guid}/enable")]
    public async Task<IActionResult> Enable(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return NotFound();
        }

        user.IsDisabled = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);
        await AuditAsync("UserEnabled", true, await CurrentUserAsync(), User.FindFirst(ClaimTypes.Email)?.Value, $"Enabled {user.Email}.");
        return NoContent();
    }

    [HttpPost("{userId:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return NotFound();
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var link = _links.BuildResetLink(Request, user.Email ?? string.Empty, token, invite: false);
        var emailSent = await TrySendPasswordResetAsync(user, link);
        await AuditAsync("AdminPasswordResetRequested", true, await CurrentUserAsync(), User.FindFirst(ClaimTypes.Email)?.Value, $"Requested a password reset for {user.Email}.");

        return Ok(new { resetUrl = link, emailSent });
    }

    [HttpPost("{userId:guid}/reset-2fa")]
    public async Task<IActionResult> ResetTwoFactor(Guid userId)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return NotFound();
        }

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);
        _ = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await _userManager.UpdateSecurityStampAsync(user);
        await _tokens.RevokeAllForUserAsync(user.Id, HttpContext.RequestAborted);
        await AuditAsync("AdminTwoFactorReset", true, await CurrentUserAsync(), User.FindFirst(ClaimTypes.Email)?.Value, $"Reset 2FA for {user.Email}.");
        return NoContent();
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] int take = 200)
    {
        var entries = await _audit.QueryAsync(Math.Clamp(take, 1, 500), HttpContext.RequestAborted);
        return Ok(entries.Select(entry => new AuthAuditDto
        {
            TimestampUtc = entry.TimestampUtc,
            EventType = entry.EventType,
            Success = entry.Success,
            Email = entry.Email,
            IpAddress = entry.IpAddress,
            Details = entry.Details
        }));
    }

    private async Task<AppUser?> CurrentUserAsync() => await _userManager.GetUserAsync(User);

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId) ? userId : null;

    private Task<int> CountActiveAdminsAsync() =>
        _userManager.Users.CountAsync(user => user.Role == AppRoles.Admin && !user.IsDisabled, HttpContext.RequestAborted);

    private static string? NormalizeRole(string role) =>
        AppRoles.All.FirstOrDefault(candidate => string.Equals(candidate, role, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> TrySendInviteAsync(string email, string link)
    {
        try
        {
            await _email.SendAsync(
                email,
                "You have been invited to fusetor.com",
                $"<p>You have been invited to fusetor.com.</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Accept the invitation</a></p><p>This link expires in one hour.</p>",
                $"Accept your fusetor.com invitation: {link}\nThis link expires in one hour.",
                HttpContext.RequestAborted);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send the invitation email; the admin can copy the link.");
            return false;
        }
    }

    private async Task<bool> TrySendPasswordResetAsync(AppUser user, string link)
    {
        try
        {
            await _email.SendAsync(
                user.Email ?? string.Empty,
                "Reset your Fusetor password",
                $"<p>An administrator requested a password reset for your account.</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Reset password</a></p><p>This link expires in one hour.</p>",
                $"Reset your Fusetor password: {link}\nThis link expires in one hour.",
                HttpContext.RequestAborted);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send the password reset email; the admin can copy the link.");
            return false;
        }
    }

    private Task AuditAsync(string eventType, bool success, AppUser? user, string? email, string? details = null) =>
        _audit.RecordAsync(eventType, success, user?.Id, email ?? user?.Email, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), details, HttpContext.RequestAborted);

    private IActionResult IdentityProblem(IdentityResult result) =>
        ValidationProblem(new ValidationProblemDetails(result.Errors
            .GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray())));
}
