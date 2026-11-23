using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Models;

public sealed class EmailStepConfig
{
    public string To { get; set; } = string.Empty;
    public string Cc { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public string ReplyTo { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsHtml { get; set; }
    public bool UseScript { get; set; }
    public bool ContinueOnError { get; set; }

    public static EmailStepConfig Parse(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig))
        {
            return new EmailStepConfig();
        }

        try
        {
            var root = JObject.Parse(stepConfig);
            return new EmailStepConfig
            {
                To = root["to"]?.ToString() ?? string.Empty,
                Cc = root["cc"]?.ToString() ?? string.Empty,
                Bcc = root["bcc"]?.ToString() ?? string.Empty,
                ReplyTo = root["replyTo"]?.ToString() ?? string.Empty,
                Subject = root["subject"]?.ToString() ?? string.Empty,
                Body = root["body"]?.ToString() ?? string.Empty,
                IsHtml = root["isHtml"]?.Value<bool>() ?? false,
                UseScript = root["useScript"]?.Value<bool>() ?? false,
                ContinueOnError = root["continueOnError"]?.Value<bool>() ?? false
            };
        }
        catch
        {
            return new EmailStepConfig();
        }
    }

    public string Write() => new JObject
    {
        ["to"] = To.Trim(),
        ["cc"] = Cc.Trim(),
        ["bcc"] = Bcc.Trim(),
        ["replyTo"] = ReplyTo.Trim(),
        ["subject"] = Subject,
        ["body"] = Body,
        ["isHtml"] = IsHtml,
        ["useScript"] = UseScript,
        ["continueOnError"] = ContinueOnError
    }.ToString(Formatting.None);
}
