using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services.Email;

// Development-only fallback. It logs the message headers so the flow can be exercised
// without SMTP; the body is kept at Debug so payloads are not emitted at Information.
public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new EmailMessage(
                new[] { to },
                Array.Empty<string>(),
                Array.Empty<string>(),
                null,
                subject,
                htmlBody,
                textBody),
            cancellationToken);

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Development email (SMTP not configured)\nTo: {To}\nCc: {Cc}\nBcc: {Bcc}\nReply-To: {ReplyTo}\nSubject: {Subject}",
            string.Join(", ", message.To),
            string.Join(", ", message.Cc),
            string.Join(", ", message.Bcc),
            message.ReplyTo ?? string.Empty,
            message.Subject);

        _logger.LogDebug("Development email body:\n{Body}", message.TextBody);
        return Task.CompletedTask;
    }
}
