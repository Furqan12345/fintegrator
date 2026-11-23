using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;

namespace SimpleIPaaS.Tests.TestDoubles;

public sealed class FakeEmailSender : IEmailSender
{
    public List<SentEmail> Messages { get; } = new();

    public Exception? ThrowOnSend { get; set; }

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
        if (ThrowOnSend != null)
        {
            throw ThrowOnSend;
        }

        Messages.Add(new SentEmail(
            string.Join(", ", message.To),
            message.Cc.ToList(),
            message.Bcc.ToList(),
            message.ReplyTo,
            message.Subject,
            message.HtmlBody,
            message.TextBody));
        return Task.CompletedTask;
    }

    public sealed record SentEmail(
        string To,
        IReadOnlyList<string> Cc,
        IReadOnlyList<string> Bcc,
        string? ReplyTo,
        string Subject,
        string HtmlBody,
        string TextBody);
}
