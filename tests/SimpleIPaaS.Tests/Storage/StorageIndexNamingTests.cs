using System;
using System.Linq;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Storage;

// The naming scheme is the contract that makes ILM and retention possible, so it is pinned here.
public sealed class StorageIndexNamingTests
{
    [Theory]
    [InlineData(StorageGranularity.Daily, "fusetor-logs-2026.10.16")]
    [InlineData(StorageGranularity.Monthly, "fusetor-logs-2026.10")]
    public void BackingIndex_UsesTheConfiguredPeriod(StorageGranularity granularity, string expected)
    {
        var index = StorageIndexNaming.BackingIndex("fusetor", StorageDatasets.Logs, granularity, new DateTime(2026, 10, 16, 13, 45, 0, DateTimeKind.Utc));

        Assert.Equal(expected, index);
    }

    [Fact]
    public void BackingIndex_UsesIsoWeekNumbers()
    {
        // 2026-01-01 is a Thursday, so it belongs to ISO week 1 of 2026.
        Assert.Equal("fusetor-logs-2026.W01", StorageIndexNaming.BackingIndex("fusetor", StorageDatasets.Logs, StorageGranularity.Weekly, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        // 2027-01-01 is a Friday and belongs to the last week of 2026.
        Assert.Equal("fusetor-logs-2026.W53", StorageIndexNaming.BackingIndex("fusetor", StorageDatasets.Logs, StorageGranularity.Weekly, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void WriteAliasAndPattern_AreDerivedFromThePrefixAndDataset()
    {
        Assert.Equal("fusetor-packet-logs-write", StorageIndexNaming.WriteAlias("fusetor", StorageDatasets.PacketLogs));
        Assert.Equal("fusetor-packet-logs-*", StorageIndexNaming.Pattern("fusetor", StorageDatasets.PacketLogs));
        Assert.Equal("fusetor-packet-logs-template", StorageIndexNaming.TemplateName("fusetor", StorageDatasets.PacketLogs));
        Assert.Equal("fusetor-packet-logs-ilm", StorageIndexNaming.PolicyName("fusetor", StorageDatasets.PacketLogs));
    }

    [Fact]
    public void PeriodOf_RoundTripsEveryGranularity()
    {
        var daily = StorageIndexNaming.PeriodOf("fusetor", StorageDatasets.Logs, "fusetor-logs-2026.10.16");
        Assert.NotNull(daily);
        Assert.Equal(new DateTime(2026, 10, 16), daily!.Value.Start);
        Assert.Equal(new DateTime(2026, 10, 17), daily.Value.End);

        var weekly = StorageIndexNaming.PeriodOf("fusetor", StorageDatasets.Logs, "fusetor-logs-2026.W42");
        Assert.NotNull(weekly);
        Assert.Equal(DayOfWeek.Monday, weekly!.Value.Start.DayOfWeek);
        Assert.Equal(7, (weekly.Value.End - weekly.Value.Start).TotalDays);

        var monthly = StorageIndexNaming.PeriodOf("fusetor", StorageDatasets.Logs, "fusetor-logs-2026.10");
        Assert.NotNull(monthly);
        Assert.Equal(new DateTime(2026, 10, 1), monthly!.Value.Start);
        Assert.Equal(new DateTime(2026, 11, 1), monthly.Value.End);
    }

    [Fact]
    public void PeriodOf_RejectsForeignNames()
    {
        Assert.Null(StorageIndexNaming.PeriodOf("fusetor", StorageDatasets.Logs, "other-logs-2026.10.16"));
        Assert.Null(StorageIndexNaming.PeriodOf("fusetor", StorageDatasets.Logs, "fusetor-packets-2026.10.16"));
        Assert.Null(StorageIndexNaming.PeriodOf("fusetor", StorageDatasets.Logs, "fusetor-logs-write"));
        Assert.Null(StorageIndexNaming.PeriodOf("fusetor", StorageDatasets.Logs, "fusetor-logs-2026.13.45"));
    }

    [Fact]
    public void IsExpired_OnlyWhenTheWholePeriodIsOlderThanTheWindow()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        // A period that ended today is inside a 3 day window.
        Assert.False(StorageIndexNaming.IsExpired((new DateTime(2026, 10, 15), new DateTime(2026, 10, 16)), 3, now));
        Assert.False(StorageIndexNaming.IsExpired((new DateTime(2026, 10, 13), new DateTime(2026, 10, 14)), 3, now));
        Assert.True(StorageIndexNaming.IsExpired((new DateTime(2026, 10, 12), new DateTime(2026, 10, 13)), 3, now));

        // Retention 0 keeps everything, whatever its age.
        Assert.False(StorageIndexNaming.IsExpired((new DateTime(2020, 1, 1), new DateTime(2020, 1, 2)), 0, now));
    }

    [Fact]
    public void CurrentIndex_FollowsTheDatasetGranularity()
    {
        var snapshot = StubStorageSettingsProvider.Snapshot(
            logsOnElasticsearch: true,
            granularity: StorageGranularity.Monthly,
            indexPrefix: "acme");

        var index = StorageIndexNaming.CurrentIndex(snapshot, StorageDatasets.Logs);

        Assert.StartsWith("acme-logs-", index);
        Assert.Equal(7, index.Length - "acme-logs-".Length);
    }

    [Fact]
    public void DatasetsUsingElasticsearch_OnlyListsEnabledDatasets()
    {
        var snapshot = StubStorageSettingsProvider.Snapshot(logsOnElasticsearch: true, crossReferencesOnElasticsearch: true);
        Assert.Equal(new[] { StorageDatasets.Logs, StorageDatasets.CrossReferences }, StorageIndexNaming.DatasetsUsingElasticsearch(snapshot));

        var disabled = StubStorageSettingsProvider.Snapshot(elasticsearchEnabled: false, logsOnElasticsearch: true);
        Assert.Empty(StorageIndexNaming.DatasetsUsingElasticsearch(disabled));
    }
}
