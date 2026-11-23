namespace SimpleIPaaS.Application.Interfaces;

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string? ReplyTo,
    string Subject,
    string HtmlBody,
    string TextBody);

public interface IEmailSender
{
    Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken = default);

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
