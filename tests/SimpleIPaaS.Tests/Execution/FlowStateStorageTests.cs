using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

// Where flow state and run history are stored: Fan Out snapshots, rename warnings, retention defaults.
public sealed class FlowStateStorageTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private FlowExecutor CreateExecutor() => new(
        _flows,
        new UnreachableTransportEngine(),
        new UnreachableCodeExecutionService(),
        _executions,
        new StubCrossReferenceRepository(),
        new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

    [Fact]
    public async Task Fan_out_stores_the_parent_state_once_however_many_children_it_creates()
    {
        // 300 orders of ~1 KB: the parent's state holds the whole array (~300 KB). Copying it into every child, as
        // before, wrote ~90 MB; the snapshot writes it once.
        var orders = string.Join(",", Enumerable.Range(1, 300).Select(i => $"{{\"id\":{i},\"note\":\"{new string('x', 1000)}\"}}"));
        var trigger = $"{{\"orders\":[{orders}]}}";
        var fanOut = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Split",
            StepType = StepType.FanOut,
            StepConfig = "{\"fanOut\":{\"arrayPath\":\"flowState.trigger.orders\",\"itemVariable\":\"order\",\"maxItems\":1000}}"
        };
        var process = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "Process", StepType = StepType.Debug };
        var flow = new IntegrationFlow { Nodes = { fanOut, process } };
        flow.Edges.Add(new IntegrationEdge { SourceNodeId = fanOut.Id, TargetNodeId = process.Id });
        _flows.Seed(flow);
        var parent = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        _executions.Seed(parent);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, parent.Id, trigger, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        var children = (await _executions.GetFlowExecutionsAsync(new Application.Models.ExecutionQuery()))
            .Where(run => run.ParentExecutionId == parent.Id)
            .OrderBy(run => run.FanOutItemIndex)
            .ToList();
        Assert.Equal(300, children.Count);
        var snapshot = Assert.Single(_executions.FanOutSnapshots.Values);
        Assert.True(snapshot.FlowStateJson.Length > 300_000);

        long childBytes = children.Sum(child => (long)(child.InitialFlowStateJson?.Length ?? 0)
            + (child.FanOutItemJson?.Length ?? 0) + (child.TriggerPayloadJson?.Length ?? 0));
        // Each child carries only its own item (twice: trigger payload and fan-out item), about 2 KB.
        Assert.True(childBytes < 300 * 2_100, $"children stored {childBytes:N0} bytes");

        // Every child still starts from the full parent state plus its own item.
        var last = Newtonsoft.Json.Linq.JObject.Parse((await FanOutState.ResolveInitialFlowStateAsync(children[^1], _executions))!);
        Assert.Equal(300, (int)last["order"]!["id"]!);
        Assert.Equal(300, (int)last["Split"]!["id"]!);
        Assert.Equal(300, last["trigger"]!["orders"]!.Count());
    }

    [Fact]
    public async Task A_child_queued_before_snapshots_still_starts_from_its_own_copy_and_a_lost_snapshot_is_reported()
    {
        var legacy = new FlowExecution { InitialFlowStateJson = "{\"order\":{\"id\":5}}", FanOutItemJson = "{\"id\":5}" };
        Assert.Equal("{\"order\":{\"id\":5}}", await FanOutState.ResolveInitialFlowStateAsync(legacy, _executions));

        var orphan = new FlowExecution { FanOutSnapshotId = Guid.NewGuid(), FanOutItemJson = "{\"id\":6}" };
        Assert.Null(await FanOutState.ResolveInitialFlowStateAsync(orphan, _executions));
    }

    [Fact]
    public void Renaming_a_card_that_is_still_referenced_returns_a_warning()
    {
        var fetchId = Guid.NewGuid();
        var mapId = Guid.NewGuid();
        var splitId = Guid.NewGuid();
        var stored = new IntegrationFlow
        {
            Nodes =
            {
                new IntegrationStep { Id = fetchId, NodeName = "FetchOrders" },
                new IntegrationStep { Id = mapId, NodeName = "Map" },
                new IntegrationStep { Id = splitId, NodeName = "Split" },
            }
        };
        var updated = new IntegrationFlowDto
        {
            HelperCode = "static object Orders(JObject s) => s[\"FetchOrders\"];",
            Nodes =
            {
                new IntegrationStepDto { Id = fetchId, NodeName = "GetOrders", StepType = "HttpAction" },
                new IntegrationStepDto { Id = mapId, NodeName = "Map", StepType = "Mapping", MappingCode = "return FlowState[\"FetchOrders\"];" },
                new IntegrationStepDto { Id = splitId, NodeName = "Split", StepType = "FanOut", StepConfig = "{\"fanOut\":{\"arrayPath\":\"flowState.FetchOrders.orders\"}}" },
            }
        };

        var warning = Assert.Single(FlowRenameCheck.FindStaleReferences(stored, updated));
        Assert.Contains("'FetchOrders' was renamed to 'GetOrders'", warning);
        Assert.Contains("'Map'", warning);
        Assert.Contains("'Split'", warning);
        Assert.Contains("the helper script", warning);
    }

    [Fact]
    public void Renames_that_break_nothing_return_no_warning()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var stored = new IntegrationFlow { Nodes = { new IntegrationStep { Id = a, NodeName = "First" }, new IntegrationStep { Id = b, NodeName = "Second" } } };

        // Unreferenced rename, and a reference that only shares a prefix with the old name.
        var renamed = new IntegrationFlowDto
        {
            Nodes =
            {
                new IntegrationStepDto { Id = a, NodeName = "Start" },
                new IntegrationStepDto { Id = b, NodeName = "Second", MappingCode = "return flowState.FirstName;" },
            }
        };
        Assert.Empty(FlowRenameCheck.FindStaleReferences(stored, renamed));

        // Two cards swapping names: every reference still resolves to a card.
        var swapped = new IntegrationFlowDto
        {
            Nodes =
            {
                new IntegrationStepDto { Id = a, NodeName = "Second", MappingCode = "return flowState.First;" },
                new IntegrationStepDto { Id = b, NodeName = "First", MappingCode = "return flowState.Second;" },
            }
        };
        Assert.Empty(FlowRenameCheck.FindStaleReferences(stored, swapped));
    }

    [Fact]
    public void A_new_installation_gets_production_retention_and_an_existing_one_keeps_its_history()
    {
        using (var fresh = NewSqliteContext(out var freshConnection))
        {
            StorageSettingsSeeder.EnsureRow(fresh, NullLogger.Instance);
            var datasets = StorageCatalog.Parse(fresh.StorageSettings.Single().DatasetsJson);
            Assert.Equal(30, datasets.Single(d => d.Dataset == StorageDatasets.Executions).RetentionDays);
            Assert.Equal(14, datasets.Single(d => d.Dataset == StorageDatasets.PacketLogs).RetentionDays);
            Assert.Equal(0, datasets.Single(d => d.Dataset == StorageDatasets.CrossReferences).RetentionDays);
            freshConnection.Dispose();
        }

        using (var existing = NewSqliteContext(out var existingConnection))
        {
            existing.IntegrationFlows.Add(new IntegrationFlow { Name = "Older flow" });
            existing.SaveChanges();

            StorageSettingsSeeder.EnsureRow(existing, NullLogger.Instance);
            var datasets = StorageCatalog.Parse(existing.StorageSettings.Single().DatasetsJson);
            Assert.Equal(0, datasets.Single(d => d.Dataset == StorageDatasets.Executions).RetentionDays);
            Assert.Equal(0, datasets.Single(d => d.Dataset == StorageDatasets.PacketLogs).RetentionDays);
            Assert.Equal(3, datasets.Single(d => d.Dataset == StorageDatasets.Logs).RetentionDays);

            // A saved setting is never touched again.
            var row = existing.StorageSettings.Single();
            row.DatasetsJson = StorageCatalog.Serialize(StorageCatalog.Defaults());
            existing.SaveChanges();
            StorageSettingsSeeder.EnsureRow(existing, NullLogger.Instance);
            Assert.Equal(30, StorageCatalog.Parse(existing.StorageSettings.AsNoTracking().Single().DatasetsJson)
                .Single(d => d.Dataset == StorageDatasets.Executions).RetentionDays);
            existingConnection.Dispose();
        }
    }

    private static IPaaSContext NewSqliteContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var context = new IPaaSContext(new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        return context;
    }
}
