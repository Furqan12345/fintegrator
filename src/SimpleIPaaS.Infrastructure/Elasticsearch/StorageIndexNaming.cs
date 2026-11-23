using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Index naming is the contract that makes retention possible: one backing index per period,
// written through an alias, so expiring a period is a single index deletion (or one ILM delete
// phase) instead of a bulk delete across the whole dataset.
//
//   <prefix>-<dataset>-<period>   fusetor-logs-2026.10.16       (Daily)
//                                 fusetor-logs-2026.W42          (Weekly, ISO week)
//                                 fusetor-logs-2026.10           (Monthly)
//
//   <prefix>-<dataset>-write      write alias, always pointing at the current period
//   <prefix>-<dataset>-*          pattern every query and every deletion uses
public static class StorageIndexNaming
{
    public static string WriteAlias(string prefix, string dataset) => $"{prefix}-{dataset}-write";

    public static string Pattern(string prefix, string dataset) => $"{prefix}-{dataset}-*";

    public static string TemplateName(string prefix, string dataset) => $"{prefix}-{dataset}-template";

    public static string PolicyName(string prefix, string dataset) => $"{prefix}-{dataset}-ilm";

    public static string BackingIndex(string prefix, string dataset, StorageGranularity granularity, DateTime utc) =>
        $"{prefix}-{dataset}-{PeriodSuffix(granularity, utc)}";

    public static string PeriodSuffix(StorageGranularity granularity, DateTime utc) => granularity switch
    {
        StorageGranularity.Monthly => utc.ToString("yyyy.MM", CultureInfo.InvariantCulture),
        StorageGranularity.Weekly => $"{ISOWeek.GetYear(utc):0000}.W{ISOWeek.GetWeekOfYear(utc):00}",
        _ => utc.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
    };

    // Half-open [start, end) period of an index name, or null when the name is not one of ours.
    public static (DateTime Start, DateTime End)? PeriodOf(string prefix, string dataset, string indexName)
    {
        var expectedPrefix = $"{prefix}-{dataset}-";
        if (!indexName.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var suffix = indexName[expectedPrefix.Length..];

        if (DateTime.TryParseExact(suffix, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
        {
            return (day.Date, day.Date.AddDays(1));
        }

        var weekMarker = suffix.IndexOf(".W", StringComparison.Ordinal);
        if (weekMarker == 4
            && int.TryParse(suffix[..weekMarker], NumberStyles.None, CultureInfo.InvariantCulture, out var isoYear)
            && int.TryParse(suffix[(weekMarker + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var isoWeek)
            && isoWeek is >= 1 and <= 53)
        {
            var start = ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday);
            return (start, start.AddDays(7));
        }

        if (DateTime.TryParseExact(suffix, "yyyy.MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month))
        {
            var start = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return (start, start.AddMonths(1));
        }

        return null;
    }

    public static DateTime PeriodStart(StorageGranularity granularity, DateTime utc) => granularity switch
    {
        StorageGranularity.Monthly => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        StorageGranularity.Weekly => ISOWeek.ToDateTime(ISOWeek.GetYear(utc), ISOWeek.GetWeekOfYear(utc), DayOfWeek.Monday).ToUniversalTime(),
        _ => utc.Date
    };

    // An index is expired when its whole period is older than the retention window, so a
    // retention of 3 days on daily indices removes the index for the day that ended 3 days ago.
    public static bool IsExpired((DateTime Start, DateTime End) period, int retentionDays, DateTime nowUtc) =>
        retentionDays > 0 && period.End <= nowUtc.AddDays(-retentionDays);

    public static int AgeInDays(DateTime? createdAtUtc, DateTime nowUtc) =>
        createdAtUtc.HasValue ? Math.Max(0, (int)(nowUtc - createdAtUtc.Value).TotalDays) : 0;

    // Name of the index a write should go to right now.
    public static string CurrentIndex(StorageSettingsSnapshot settings, string dataset) =>
        BackingIndex(settings.IndexPrefix, dataset, settings.For(dataset).Granularity, DateTime.UtcNow);

    public static IReadOnlyList<string> DatasetsUsingElasticsearch(StorageSettingsSnapshot settings) =>
        StorageDatasets.All.Where(dataset => settings.UsesElasticsearch(dataset)).ToList();
}
