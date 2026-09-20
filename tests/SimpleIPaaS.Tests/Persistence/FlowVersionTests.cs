using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class FlowVersionTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public FlowVersionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options, new StubTenantContext(Tenant));

    [Fact]
    public async Task PublishCreatesImmutableVersionsAndEditingActiveFlowReturnsToDraft()
    {
        var flowId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context, new StubTenantContext(Tenant));
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                TenantId = Tenant,
                Name = "Version one",
                Nodes =
                {
                    new IntegrationStep
                    {
                        Id = Guid.NewGuid(),
                        TenantId = Tenant,
                        FlowId = flowId,
                        StepType = StepType.Mapping,
                        NodeName = "Map",
                        EndpointUrl = "https://v1.example.test"
                    }
                }
            });
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context, new StubTenantContext(Tenant));
            var first = await repository.PublishAsync(flowId, "Initial release");

            Assert.NotNull(first);
            Assert.Equal(1, first!.VersionNumber);
            Assert.Equal("Initial release", first.ChangeNote);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context, new StubTenantContext(Tenant));
            var draft = await repository.GetByIdAsync(flowId);
            Assert.NotNull(draft);
            draft!.Name = "Version two";
            draft.Nodes.Single().EndpointUrl = "https://v2.example.test";

            Assert.True(await repository.UpdateAsync(draft));
            Assert.Equal(FlowStatus.Draft, (await repository.GetByIdAsync(flowId))!.Status);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context, new StubTenantContext(Tenant));
            var second = await repository.PublishAsync(flowId, "Second release");

            Assert.NotNull(second);
            Assert.Equal(2, second!.VersionNumber);
            Assert.Equal(2, (await repository.GetVersionsAsync(flowId)).Count);
        }
    }

    [Fact]
    public async Task RollbackRestoresTargetSnapshotAndAppendsANewVersion()
    {
        var flowId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context, new StubTenantContext(Tenant));
            await repository.AddAsync(new IntegrationFlow
            {
                Id = flowId,
                TenantId = Tenant,
                Name = "Original",
                Nodes =
                {
                    new IntegrationStep
                    {
                        Id = Guid.NewGuid(),
                        TenantId = Tenant,
                        FlowId = flowId,
                        StepType = StepType.Mapping,
                        NodeName = "Map",
                        EndpointUrl = "https://original.example.test"
                    }
                }
            });
            await repository.PublishAsync(flowId);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context, new StubTenantContext(Tenant));
            var draft = await repository.GetByIdAsync(flowId);
            draft!.Name = "Changed";
            draft.Nodes.Single().EndpointUrl = "https://changed.example.test";
            await repository.UpdateAsync(draft);
            await repository.PublishAsync(flowId);
        }

        using (var context = CreateContext())
        {
            var repository = new IntegrationRepository(context, new StubTenantContext(Tenant));
            var restored = await repository.RollbackAsync(flowId, 1);

            Assert.NotNull(restored);
            Assert.Equal("Original", restored!.Name);
            Assert.Equal("https://original.example.test", restored.Nodes.Single().EndpointUrl);
            Assert.Equal(3, restored.PublishedVersion);
            Assert.Equal(FlowStatus.Active, restored.Status);

            var versions = await repository.GetVersionsAsync(flowId);
            Assert.Equal(new[] { 3, 2, 1 }, versions.Select(version => version.VersionNumber).ToArray());
            Assert.Equal(1, versions[0].RolledBackFromVersion);
        }
    }

    public void Dispose() => _connection.Dispose();
}