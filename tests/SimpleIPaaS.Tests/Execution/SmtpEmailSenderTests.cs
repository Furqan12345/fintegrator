using System.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Services.Email;

namespace SimpleIPaaS.Tests.Execution;

public sealed class SmtpEmailSenderTests
{
    [Fact]
    public void BuildMessageSetsToCcBccAndReplyTo()
    {
        var options = new EmailOptions
        {
            FromAddress = "from@example.com",
            FromName = "Fusetor"
        };
        using var message = SmtpEmailSender.BuildMessage(
            options,
            new EmailMessage(
                new[] { "a@example.com", "b@example.com" },
                new[] { "c@example.com" },
                new[] { "blind@example.com" },
                "reply@example.com",
                "Subject",
                "<b>Hi</b>",
                "Hi"));

        Assert.Equal(new[] { "a@example.com", "b@example.com" }, message.To.Select(address => address.Address));
        Assert.Equal(new[] { "c@example.com" }, message.CC.Select(address => address.Address));
        Assert.Equal(new[] { "blind@example.com" }, message.Bcc.Select(address => address.Address));
        Assert.Equal("reply@example.com", message.ReplyToList.Single().Address);
        Assert.Equal("Subject", message.Subject);
        Assert.True(message.IsBodyHtml);
        Assert.Equal("<b>Hi</b>", message.Body);
        Assert.Equal("text/plain", Assert.Single(message.AlternateViews).ContentType.MediaType);
    }
}
