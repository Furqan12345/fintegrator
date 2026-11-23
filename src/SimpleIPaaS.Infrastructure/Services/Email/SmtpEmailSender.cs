using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Infrastructure.Services.Email;

public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger)
    {
        _options = options;
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

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new InvalidOperationException("Email:Smtp:Host is not configured.");
        }

#pragma warning disable CS0618 // SmtpClient is obsolete but remains supported and sufficient here.
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        using var mail = BuildMessage(_options, message);
        await client.SendMailAsync(mail, cancellationToken);
#pragma warning restore CS0618

        _logger.LogInformation(
            "Sent {Subject} email to {RecipientCount} recipient(s).",
            message.Subject,
            message.To.Count + message.Cc.Count + message.Bcc.Count);
    }

    // Separated from SendAsync so the exact To/CC/BCC/Reply-To shape can be tested
    // without opening an SMTP connection.
    public static MailMessage BuildMessage(EmailOptions options, EmailMessage message)
    {
        var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromName),
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsBodyHtml = true
        };

        foreach (var address in message.To) mail.To.Add(address);
        foreach (var address in message.Cc) mail.CC.Add(address);
        foreach (var address in message.Bcc) mail.Bcc.Add(address);
        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            mail.ReplyToList.Add(new MailAddress(message.ReplyTo));
        }

        mail.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(message.TextBody, null, "text/plain"));
        return mail;
    }
}
