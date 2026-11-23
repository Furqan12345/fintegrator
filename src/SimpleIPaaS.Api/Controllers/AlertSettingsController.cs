using System;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Services.Email;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

// Host-wide failure-alert settings. Admin-only: this controls where operational
// notifications leave the deployment.
[ApiController]
[Route("api/alert-settings")]
[Authorize(Policy = "Admin")]
public sealed class AlertSettingsController : ControllerBase
{
    private const int MaxRecipients = 10;

    private readonly IPaaSContext _context;
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<AlertSettingsController> _logger;

    public AlertSettingsController(
        IPaaSContext context,
        IEmailSender emailSender,
        EmailOptions emailOptions,
        ILogger<AlertSettingsController> logger)
    {
        _context = context;
        _emailSender = emailSender;
        _emailOptions = emailOptions;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<ActionResult<AlertSettingsDto>> Get(CancellationToken cancellationToken)
    {
        var settings = await _context.AlertSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == 1, cancellationToken);

        return Ok(ToDto(settings));
    }

    [HttpPut("")]
    public async Task<ActionResult<AlertSettingsDto>> Update(
        [FromBody] AlertSettingsUpdateDto request,
        CancellationToken cancellationToken)
    {
        if (request.CooldownMinutes is < 1 or > 1440)
        {
            return BadRequest(new { message = "Cooldown must be between 1 and 1440 minutes." });
        }

        var recipients = FailureAlertService.ParseRecipients(request.Recipients);
        if (recipients.Count > MaxRecipients)
        {
            return BadRequest(new { message = $"Add at most {MaxRecipients} recipients." });
        }

        foreach (var recipient in recipients)
        {
            if (!MailAddress.TryCreate(recipient, out _))
            {
                return BadRequest(new { message = $"'{recipient}' is not a valid email address." });
            }
        }

        if (request.Enabled && recipients.Count == 0)
        {
            return BadRequest(new { message = "Add at least one recipient before enabling alerts." });
        }

        var settings = await _context.AlertSettings.FirstOrDefaultAsync(row => row.Id == 1, cancellationToken);
        var wasEnabled = settings?.Enabled ?? false;

        if (settings == null)
        {
            settings = new AlertSettings();
            _context.AlertSettings.Add(settings);
        }

        settings.Enabled = request.Enabled;
        settings.Recipients = string.Join("; ", recipients);
        settings.CooldownMinutes = request.CooldownMinutes;
        settings.UpdatedAt = DateTime.UtcNow;

        // Turning alerts on starts a new window: failures from before this moment are
        // deliberately never emailed, so the owner does not get the entire backlog.
        if (request.Enabled && !wasEnabled)
        {
            settings.EnabledAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(settings));
    }

    [HttpPost("test")]
    [EnableRateLimiting("alert-test")]
    public async Task<IActionResult> SendTest(CancellationToken cancellationToken)
    {
        var settings = await _context.AlertSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == 1, cancellationToken);
        var recipients = FailureAlertService.ParseRecipients(settings?.Recipients);
        if (recipients.Count == 0)
        {
            return BadRequest(new { message = "Save at least one recipient before sending a test email." });
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.Host))
        {
            return BadRequest(new
            {
                message = "SMTP is not configured. Set Email:Smtp:Host for both the API and the Engine, then try again."
            });
        }

        const string subject = "Fusetor alert test";
        const string html = "<p>This is a test alert from Fusetor.</p><p>If you can read this, failure alerts can reach you.</p>";
        const string text = "This is a test alert from Fusetor.\n\nIf you can read this, failure alerts can reach you.";

        try
        {
            foreach (var recipient in recipients)
            {
                await _emailSender.SendAsync(recipient, subject, html, text, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send the alert test email.");
            return BadRequest(new { message = "Could not send the test email: " + ex.Message });
        }

        return Ok(new { message = $"Test email sent to {recipients.Count} recipient(s)." });
    }

    private AlertSettingsDto ToDto(AlertSettings? settings) => new()
    {
        Enabled = settings?.Enabled ?? false,
        Recipients = settings?.Recipients ?? string.Empty,
        CooldownMinutes = settings?.CooldownMinutes ?? 15,
        EnabledAt = settings?.EnabledAt,
        LastSentAt = settings?.LastSentAt,
        UpdatedAt = settings?.UpdatedAt,
        SmtpConfigured = !string.IsNullOrWhiteSpace(_emailOptions.Host)
    };
}
