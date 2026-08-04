using System;
using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Models;

public class ForEachStepConfig
{
    public string ArrayPath { get; set; } = string.Empty;
    public string ItemVariable { get; set; } = "item";
    public bool Parallel { get; set; } = false;
    public int MaxDegreeOfParallelism { get; set; } = 1;

    public static ForEachStepConfig Parse(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig))
        {
            return new ForEachStepConfig();
        }

        JObject root;
        try
        {
            root = JObject.Parse(stepConfig);
        }
        catch (Exception)
        {
            return new ForEachStepConfig();
        }

        var scope = root["forEach"] as JObject ?? root;

        var config = new ForEachStepConfig
        {
            ArrayPath = (scope["arrayPath"]?.ToString() ?? string.Empty).Trim(),
            ItemVariable = (scope["itemVariable"]?.ToString() ?? "item").Trim(),
            Parallel = scope["parallel"]?.Value<bool>() ?? false,
            MaxDegreeOfParallelism = scope["maxDegreeOfParallelism"]?.Value<int>() ?? 1
        };

        if (string.IsNullOrWhiteSpace(config.ItemVariable))
        {
            config.ItemVariable = "item";
        }

        if (config.MaxDegreeOfParallelism < 1)
        {
            config.MaxDegreeOfParallelism = 1;
        }

        return config;
    }
}
