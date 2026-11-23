using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Models;

public sealed class FanOutStepConfig
{
    public const int DefaultMaxItems = 1000;
    public const int AbsoluteMaxItems = 10000;

    public string ArrayPath { get; set; } = string.Empty;
    public string ItemVariable { get; set; } = "item";
    public int MaxItems { get; set; } = DefaultMaxItems;

    public static FanOutStepConfig Parse(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig))
        {
            return new FanOutStepConfig();
        }

        try
        {
            var root = JObject.Parse(stepConfig);
            var scope = root["fanOut"] as JObject ?? root;
            var config = new FanOutStepConfig
            {
                ArrayPath = (scope["arrayPath"]?.ToString() ?? string.Empty).Trim(),
                ItemVariable = (scope["itemVariable"]?.ToString() ?? "item").Trim(),
                MaxItems = scope["maxItems"]?.Value<int>() ?? DefaultMaxItems
            };

            if (string.IsNullOrWhiteSpace(config.ItemVariable))
            {
                config.ItemVariable = "item";
            }

            config.MaxItems = Math.Clamp(config.MaxItems, 1, AbsoluteMaxItems);
            return config;
        }
        catch
        {
            return new FanOutStepConfig();
        }
    }
}
