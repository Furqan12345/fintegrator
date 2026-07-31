using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Models;

public class CrossReferenceStepConfig
{
    public string ListName { get; set; } = string.Empty;
    public string ArrayPath { get; set; } = string.Empty;
    public IReadOnlyList<string> KeyPaths { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ValuePaths { get; set; } = Array.Empty<string>();

    public static CrossReferenceStepConfig Parse(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig))
        {
            return new CrossReferenceStepConfig();
        }

        JObject root;
        try
        {
            root = JObject.Parse(stepConfig);
        }
        catch (Exception)
        {
            return new CrossReferenceStepConfig();
        }

        var scope = root["crossReference"] as JObject ?? root;

        return new CrossReferenceStepConfig
        {
            ListName = (scope["listName"]?.ToString() ?? string.Empty).Trim(),
            ArrayPath = (scope["arrayPath"]?.ToString() ?? string.Empty).Trim(),
            KeyPaths = ReadPaths(scope["keyPaths"]),
            ValuePaths = ReadPaths(scope["valuePaths"])
        };
    }

    private static IReadOnlyList<string> ReadPaths(JToken? token)
    {
        if (token is JArray array)
        {
            return array
                .Select(item => (item?.ToString() ?? string.Empty).Trim())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
        }

        var single = (token?.ToString() ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single };
    }
}
