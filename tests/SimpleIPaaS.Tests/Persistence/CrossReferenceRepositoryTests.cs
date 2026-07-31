using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class CrossReferenceRepositoryTests : IDisposable
{
    private const string ListName = "processed-orders";

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public CrossReferenceRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(_connection)
            .Options;

        using var seedContext = CreateContext(TenantA);
        seedContext.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext(Guid tenantId) => new(_options, new StubTenantContext(tenantId));

    private CrossReferenceRepository CreateRepository(IPaaSContext context, Guid tenantId) =>
        new(context, new StubTenantContext(tenantId));

    private static IReadOnlyCollection<CrossReferenceEntry> Entries(params string[] keys) =>
        keys.Select(key => new CrossReferenceEntry { ListName = ListName, KeyValue = key, ValueJson = "{}" }).ToList();

    [Fact]
    public async Task UpsertEntriesAsync_IsIdempotentAcrossRuns()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        await repository.EnsureListAsync(ListName, "Orders already handled");

        var first = await repository.UpsertEntriesAsync(ListName, Entries("1", "2", "3", "4", "5"));
        var second = await repository.UpsertEntriesAsync(ListName, Entries("1", "2", "3", "4", "5"));

        Assert.Equal(5, first);
        Assert.Equal(0, second);

        var page = await repository.GetEntriesAsync(ListName, null, 1, 50);
        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task UpsertEntriesAsync_CollapsesDuplicateKeysWithinTheSameBatch()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        var inserted = await repository.UpsertEntriesAsync(ListName, Entries("1", "1", "2"));

        Assert.Equal(2, inserted);
    }

    [Fact]
    public async Task GetExistingKeysAsync_ReturnsOnlyTheKeysAlreadyStored()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        await repository.UpsertEntriesAsync(ListName, Entries("1", "2"));

        var existing = await repository.GetExistingKeysAsync(ListName, new[] { "1", "2", "3", "4", "5" });

        Assert.Equal(new[] { "1", "2" }, existing.OrderBy(key => key));
    }

    [Fact]
    public async Task Entries_AreIsolatedPerTenant()
    {
        using (var contextA = CreateContext(TenantA))
        {
            await CreateRepository(contextA, TenantA).UpsertEntriesAsync(ListName, Entries("1"));
        }

        using var contextB = CreateContext(TenantB);
        var repositoryB = CreateRepository(contextB, TenantB);

        Assert.Empty(await repositoryB.GetExistingKeysAsync(ListName, new[] { "1" }));
        Assert.Equal(1, await repositoryB.UpsertEntriesAsync(ListName, Entries("1")));
    }

    [Fact]
    public async Task ClearListAsync_RemovesEveryEntryButKeepsTheList()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        await repository.EnsureListAsync(ListName, string.Empty);
        await repository.UpsertEntriesAsync(ListName, Entries("1", "2", "3"));

        Assert.Equal(3, await repository.ClearListAsync(ListName));
        Assert.NotNull(await repository.GetListAsync(ListName));
        Assert.Equal(0, (await repository.GetEntriesAsync(ListName, null, 1, 50)).TotalCount);
    }

    [Fact]
    public async Task GetListsAsync_ReportsEntryCounts()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        await repository.EnsureListAsync(ListName, string.Empty);
        await repository.UpsertEntriesAsync(ListName, Entries("1", "2"));

        var summary = Assert.Single(await repository.GetListsAsync());

        Assert.Equal(ListName, summary.List.Name);
        Assert.Equal(2, summary.EntryCount);
    }

    [Fact]
    public async Task GetEntriesAsync_FiltersBySearchTerm()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        await repository.UpsertEntriesAsync(ListName, Entries("order-1", "order-2", "invoice-1"));

        var page = await repository.GetEntriesAsync(ListName, "invoice", 1, 50);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("invoice-1", Assert.Single(page.Entries).KeyValue);
    }

    [Fact]
    public async Task DeleteEntryAsync_RemovesASingleKey()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        await repository.UpsertEntriesAsync(ListName, Entries("1", "2"));

        var target = (await repository.GetEntriesAsync(ListName, "1", 1, 50)).Entries.Single();

        Assert.True(await repository.DeleteEntryAsync(target.Id));
        Assert.False(await repository.DeleteEntryAsync(target.Id));
        Assert.Equal(1, (await repository.GetEntriesAsync(ListName, null, 1, 50)).TotalCount);
    }

    [Fact]
    public async Task DeleteListAsync_RemovesTheListAndItsEntries()
    {
        using var context = CreateContext(TenantA);
        var repository = CreateRepository(context, TenantA);

        await repository.EnsureListAsync(ListName, string.Empty);
        await repository.UpsertEntriesAsync(ListName, Entries("1", "2"));

        Assert.True(await repository.DeleteListAsync(ListName));
        Assert.Null(await repository.GetListAsync(ListName));
        Assert.Equal(0, (await repository.GetEntriesAsync(ListName, null, 1, 50)).TotalCount);
    }

    public void Dispose() => _connection.Dispose();
}
