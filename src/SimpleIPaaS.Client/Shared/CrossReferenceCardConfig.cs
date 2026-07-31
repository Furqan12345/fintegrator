using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimpleIPaaS.Client.Shared;

public class CrossReferenceCardConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public class PathEntry
    {
        public string Value { get; set; } = string.Empty;
    }

    public string ListName { get; set; } = string.Empty;
    public string ArrayPath { get; set; } = string.Empty;
    public List<PathEntry> KeyPaths { get; set; } = new() { new PathEntry() };
    public List<PathEntry> ValuePaths { get; set; } = new();
    public bool IncludeValuePaths { get; set; }

    public static CrossReferenceCardConfig Read(string? stepConfig, bool valuePaths)
    {
        var root = Parse(stepConfig);

        var config = new CrossReferenceCardConfig
        {
            ListName = root["listName"]?.GetValue<string>() ?? string.Empty,
            ArrayPath = root["arrayPath"]?.GetValue<string>() ?? string.Empty,
            KeyPaths = ReadPaths(root["keyPaths"]),
            ValuePaths = ReadPaths(root["valuePaths"], padEmpty: false),
            IncludeValuePaths = valuePaths
        };

        return config;
    }

    public string Write(string? stepConfig)
    {
        var root = Parse(stepConfig);
        root["listName"] = ListName?.Trim() ?? string.Empty;
        root["arrayPath"] = ArrayPath?.Trim() ?? string.Empty;
        root["keyPaths"] = WritePaths(KeyPaths);

        if (IncludeValuePaths)
        {
            root["valuePaths"] = WritePaths(ValuePaths);
        }

        return root.ToJsonString(JsonOptions);
    }

    private static JsonArray WritePaths(IEnumerable<PathEntry> paths)
    {
        var array = new JsonArray();

        foreach (var path in paths.Where(entry => !string.IsNullOrWhiteSpace(entry.Value)))
        {
            array.Add(JsonValue.Create(path.Value.Trim()));
        }

        return array;
    }

    private static List<PathEntry> ReadPaths(JsonNode? node, bool padEmpty = true)
    {
        var paths = new List<PathEntry>();

        if (node is JsonArray array)
        {
            paths.AddRange(array
                .Select(item => item?.GetValue<string>() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new PathEntry { Value = value }));
        }

        if (paths.Count == 0 && padEmpty)
        {
            paths.Add(new PathEntry());
        }

        return paths;
    }

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
