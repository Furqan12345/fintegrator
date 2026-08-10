using System;
using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Models;

public sealed class PaginationStepConfig
{
    public string Style { get; init; } = "None";
    public int MaxPages { get; init; } = 100;
    public int DelayMs { get; init; }
    public string AggregatePath { get; init; } = string.Empty;
    public string NextTokenPath { get; init; } = "pagination.nextToken";
    public string NextTokenParameter { get; init; } = "NextToken";
    public string NextLinkHeader { get; init; } = "Link";
    public string CursorResponsePath { get; init; } = string.Empty;
    public string CursorRequestParameter { get; init; } = "cursor";
    public string OffsetRequestParameter { get; init; } = "offset";
    public string PageSizeParameter { get; init; } = "limit";
    public int PageSize { get; init; } = 100;
    public string HasMoreResponsePath { get; init; } = string.Empty;

    public bool Enabled => !string.Equals(Style, "None", StringComparison.OrdinalIgnoreCase);

    public static PaginationStepConfig Parse(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig))
        {
            return new PaginationStepConfig();
        }

        try
        {
            var root = JObject.Parse(stepConfig);
            var pagination = root["pagination"] as JObject;
            if (pagination == null)
            {
                return new PaginationStepConfig();
            }

            return new PaginationStepConfig
            {
                Style = pagination["style"]?.ToString() ?? "None",
                MaxPages = Math.Clamp(pagination["maxPages"]?.Value<int>() ?? 100, 1, 1000),
                DelayMs = Math.Clamp(pagination["delayMs"]?.Value<int>() ?? 0, 0, 60000),
                AggregatePath = pagination["aggregatePath"]?.ToString() ?? string.Empty,
                NextTokenPath = pagination["nextTokenPath"]?.ToString() ?? "pagination.nextToken",
                NextTokenParameter = pagination["nextTokenParameter"]?.ToString() ?? "NextToken",
                NextLinkHeader = pagination["nextLinkHeader"]?.ToString() ?? "Link",
                CursorResponsePath = pagination["cursorResponsePath"]?.ToString() ?? string.Empty,
                CursorRequestParameter = pagination["cursorRequestParameter"]?.ToString() ?? "cursor",
                OffsetRequestParameter = pagination["offsetRequestParameter"]?.ToString() ?? "offset",
                PageSizeParameter = pagination["pageSizeParameter"]?.ToString() ?? "limit",
                PageSize = Math.Clamp(pagination["pageSize"]?.Value<int>() ?? 100, 1, 10000),
                HasMoreResponsePath = pagination["hasMoreResponsePath"]?.ToString() ?? string.Empty
            };
        }
        catch
        {
            return new PaginationStepConfig();
        }
    }
}
