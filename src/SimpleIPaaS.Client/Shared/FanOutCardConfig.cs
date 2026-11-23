using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleIPaaS.Client.Shared;

public sealed class FanOutCardConfig
{
    public string ArrayPath { get; set; } = string.Empty;
    public string ItemVariable { get; set; } = "item";
    public int MaxItems { get; set; } = 1000;

    public static FanOutCardConfig Read(string? stepConfig)
    {
        var root = Parse(stepConfig);
        var scope = root["fanOut"] as JsonObject ?? root;
        return new FanOutCardConfig
        {
            ArrayPath = scope["arrayPath"]?.GetValue<string>() ?? string.Empty,
            ItemVariable = scope["itemVariable"]?.GetValue<string>() ?? "item",
            MaxItems = Math.Clamp(scope["maxItems"]?.GetValue<int>() ?? 1000, 1, 10000)
        };
    }

    public string Write(string? stepConfig)
    {
        var root = Parse(stepConfig);
        var scope = root["fanOut"] as JsonObject ?? new JsonObject();
        scope["arrayPath"] = ArrayPath?.Trim() ?? string.Empty;
        scope["itemVariable"] = string.IsNullOrWhiteSpace(ItemVariable) ? "item" : ItemVariable.Trim();
        scope["maxItems"] = Math.Clamp(MaxItems, 1, 10000);
        root["fanOut"] = scope;
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
