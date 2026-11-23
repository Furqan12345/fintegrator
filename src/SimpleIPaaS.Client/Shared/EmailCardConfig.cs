using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleIPaaS.Client.Shared;

public sealed class EmailCardConfig
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

    public static EmailCardConfig Read(string? stepConfig)
    {
        var root = Parse(stepConfig);
        return new EmailCardConfig
        {
            To = root["to"]?.GetValue<string>() ?? string.Empty,
            Cc = root["cc"]?.GetValue<string>() ?? string.Empty,
            Bcc = root["bcc"]?.GetValue<string>() ?? string.Empty,
            ReplyTo = root["replyTo"]?.GetValue<string>() ?? string.Empty,
            Subject = root["subject"]?.GetValue<string>() ?? string.Empty,
            Body = root["body"]?.GetValue<string>() ?? string.Empty,
            IsHtml = root["isHtml"]?.GetValue<bool>() ?? false,
            UseScript = root["useScript"]?.GetValue<bool>() ?? false,
            ContinueOnError = root["continueOnError"]?.GetValue<bool>() ?? false
        };
    }

    public string Write(string? stepConfig)
    {
        var root = Parse(stepConfig);
        root["to"] = To.Trim();
        root["cc"] = Cc.Trim();
        root["bcc"] = Bcc.Trim();
        root["replyTo"] = ReplyTo.Trim();
        root["subject"] = Subject;
        root["body"] = Body;
        root["isHtml"] = IsHtml;
        root["useScript"] = UseScript;
        root["continueOnError"] = ContinueOnError;
        return root.ToJsonString(JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static JsonObject Parse(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig)) return new JsonObject();
        try { return JsonNode.Parse(stepConfig) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}
