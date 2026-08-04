using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleIPaaS.Client.Shared;

public class ForEachCardConfig
{
    public string ArrayPath { get; set; } = string.Empty;
    public string ItemVariable { get; set; } = "item";
    public bool Parallel { get; set; } = false;
    public int MaxDegreeOfParallelism { get; set; } = 1;

    public static ForEachCardConfig Read(string? stepConfig)
    {
        var root = Parse(stepConfig);
        var scope = root["forEach"] as JsonObject ?? root;

        return new ForEachCardConfig
        {
            ArrayPath = scope["arrayPath"]?.GetValue<string>() ?? string.Empty,
            ItemVariable = scope["itemVariable"]?.GetValue<string>() ?? "item",
            Parallel = scope["parallel"]?.GetValue<bool>() ?? false,
            MaxDegreeOfParallelism = scope["maxDegreeOfParallelism"]?.GetValue<int>() ?? 1
        };
    }

    public string Write(string? stepConfig)
    {
        var root = Parse(stepConfig);
        var scope = root["forEach"] as JsonObject;
        if (scope == null)
        {
            scope = new JsonObject();
            root["forEach"] = scope;
        }

        scope["arrayPath"] = ArrayPath?.Trim() ?? string.Empty;
        scope["itemVariable"] = string.IsNullOrWhiteSpace(ItemVariable) ? "item" : ItemVariable.Trim();
        scope["parallel"] = Parallel;
        scope["maxDegreeOfParallelism"] = MaxDegreeOfParallelism < 1 ? 1 : MaxDegreeOfParallelism;

        root["forEach"] = scope;

        return root.ToJsonString(JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static JsonObject Parse(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(stepConfig) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
