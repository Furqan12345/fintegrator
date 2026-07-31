using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public class CrossReferenceKeyBuilderTests
{
    private static readonly JObject Record = JObject.Parse("""
        {
            "id": 42,
            "order": { "id": "A-1", "lineNo": 7, "note": null },
            "tags": ["red", "blue"],
            "active": true
        }
        """);

    [Fact]
    public void BuildKey_ReadsATopLevelField()
    {
        Assert.Equal("42", CrossReferenceKeyBuilder.BuildKey(Record, new[] { "id" }));
    }

    [Fact]
    public void BuildKey_ReadsANestedDottedPath()
    {
        Assert.Equal("A-1", CrossReferenceKeyBuilder.BuildKey(Record, new[] { "order.id" }));
    }

    [Fact]
    public void BuildKey_CombinesSeveralPathsIntoACompositeKey()
    {
        Assert.Equal("A-1|7", CrossReferenceKeyBuilder.BuildKey(Record, new[] { "order.id", "order.lineNo" }));
    }

    [Fact]
    public void BuildKey_TreatsMissingAndNullFieldsAsEmptySegments()
    {
        Assert.Equal("A-1||", CrossReferenceKeyBuilder.BuildKey(Record, new[] { "order.id", "order.note", "order.missing" }));
    }

    [Fact]
    public void BuildKey_ReturnsEmptyWhenEveryPathIsMissing()
    {
        Assert.Equal(string.Empty, CrossReferenceKeyBuilder.BuildKey(Record, new[] { "missing", "order.missing" }));
    }

    [Fact]
    public void BuildKey_ReturnsEmptyWhenNoPathsAreConfigured()
    {
        Assert.Equal(string.Empty, CrossReferenceKeyBuilder.BuildKey(Record, Array.Empty<string>()));
    }

    [Fact]
    public void BuildKey_ResolvesArrayIndexes()
    {
        Assert.Equal("blue", CrossReferenceKeyBuilder.BuildKey(Record, new[] { "tags.1" }));
        Assert.Equal("red", CrossReferenceKeyBuilder.BuildKey(Record, new[] { "tags[0]" }));
    }

    [Fact]
    public void BuildKey_EscapesTheSeparatorInsideValues()
    {
        var record = JObject.Parse("""{ "a": "x|y", "b": "z" }""");
        Assert.Equal("x\\|y|z", CrossReferenceKeyBuilder.BuildKey(record, new[] { "a", "b" }));
    }

    [Fact]
    public void BuildKey_FormatsNonStringScalarsInvariantly()
    {
        Assert.Equal("true", CrossReferenceKeyBuilder.BuildKey(Record, new[] { "active" }));
    }

    [Fact]
    public void ToRecords_UnwrapsArraysAndWrapsSingleObjects()
    {
        Assert.Equal(2, CrossReferenceKeyBuilder.ToRecords(JArray.Parse("[{},{}]")).Count);
        Assert.Single(CrossReferenceKeyBuilder.ToRecords(Record));
        Assert.Empty(CrossReferenceKeyBuilder.ToRecords(null));
    }

    [Fact]
    public void BuildValueJson_ProjectsTheConfiguredPaths()
    {
        var json = CrossReferenceKeyBuilder.BuildValueJson(Record, new[] { "order.lineNo", "missing" });
        var parsed = JObject.Parse(json);

        Assert.Equal(7, parsed["lineNo"]!.Value<int>());
        Assert.Equal(JTokenType.Null, parsed["missing"]!.Type);
    }

    [Fact]
    public void Parse_ReadsTheStepConfigSchema()
    {
        var config = CrossReferenceStepConfig.Parse("""
            { "listName": "processed-orders", "arrayPath": "orders",
              "keyPaths": ["id"], "valuePaths": ["status", "updatedAt"] }
            """);

        Assert.Equal("processed-orders", config.ListName);
        Assert.Equal("orders", config.ArrayPath);
        Assert.Equal(new[] { "id" }, config.KeyPaths);
        Assert.Equal(new[] { "status", "updatedAt" }, config.ValuePaths);
    }

    [Fact]
    public void Parse_ReturnsAnEmptyConfigForMalformedJson()
    {
        var config = CrossReferenceStepConfig.Parse("not json");

        Assert.Equal(string.Empty, config.ListName);
        Assert.Empty(config.KeyPaths);
    }
}

public class CrossReferenceNodeTests
{
    private const string ListName = "processed-orders";

    private const string Payload = """
        { "orders": [
            { "id": "1", "status": "new" },
            { "id": "2", "status": "new" },
            { "id": "3", "status": "new" },
            { "id": "4", "status": "new" },
            { "id": "5", "status": "new" }
        ] }
        """;

    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();
    private readonly StubCrossReferenceRepository _crossReferences = new();

    private FlowExecutor CreateExecutor() => new(
        _flows,
        new UnreachableTransportEngine(),
        new UnreachableCodeExecutionService(),
        _executions,
        _crossReferences,
        NullLogger<FlowExecutor>.Instance);

    private static IntegrationStep Node(string name, StepType stepType) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = stepType,
        StepConfig = """
            { "listName": "processed-orders", "arrayPath": "orders",
              "keyPaths": ["id"], "valuePaths": ["status"] }
            """
    };

    private FlowExecution SeedExecution(IntegrationFlow flow)
    {
        _flows.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TenantId = flow.TenantId, TriggerSource = "Test" };
        _executions.Seed(execution);
        return execution;
    }

    [Fact]
    public async Task CrossReferenceFilter_PassesOnlyTheRecordsThatAreNotAlreadyStored()
    {
        _crossReferences.SeedKeys(ListName, "1", "2");

        var filter = Node("SkipProcessed", StepType.CrossReferenceFilter);
        var flow = new IntegrationFlow { Nodes = { filter } };
        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, Payload, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);

        var step = Assert.Single(_executions.StepExecutions);
        var emitted = JArray.Parse(step.ResponsePayload);

        Assert.Equal(3, emitted.Count);
        Assert.Equal(new[] { "3", "4", "5" }, emitted.Select(record => record["id"]!.ToString()));
    }

    [Fact]
    public async Task CrossReferenceFilter_PassesEveryRecordWhenTheListIsEmpty()
    {
        var filter = Node("SkipProcessed", StepType.CrossReferenceFilter);
        var flow = new IntegrationFlow { Nodes = { filter } };
        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, Payload, CancellationToken.None);

        var step = Assert.Single(_executions.StepExecutions);
        Assert.Equal(5, JArray.Parse(step.ResponsePayload).Count);
    }

    [Fact]
    public async Task CrossReferenceStore_StoresEveryKeyOnceAndIsIdempotentOnReRun()
    {
        var store = Node("RememberOrders", StepType.CrossReferenceStore);
        var flow = new IntegrationFlow { Nodes = { store } };

        var first = SeedExecution(flow);
        await CreateExecutor().ExecuteFlowAsync(flow.Id, first.Id, Payload, CancellationToken.None);

        Assert.Equal(5, _crossReferences.Entries.Count);
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, _crossReferences.Entries.Select(e => e.KeyValue));
        Assert.All(_crossReferences.Entries, entry => Assert.Equal(ListName, entry.ListName));

        var second = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        _executions.Seed(second);
        await CreateExecutor().ExecuteFlowAsync(flow.Id, second.Id, Payload, CancellationToken.None);

        Assert.Equal(5, _crossReferences.Entries.Count);
    }

    [Fact]
    public async Task CrossReferenceStore_PassesThePayloadThroughUnchangedAndActivatesDownstreamNodes()
    {
        var store = Node("RememberOrders", StepType.CrossReferenceStore);
        var downstream = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "Inspect", StepType = StepType.Debug };

        var flow = new IntegrationFlow { Nodes = { store, downstream } };
        flow.Edges.Add(new IntegrationEdge { SourceNodeId = store.Id, TargetNodeId = downstream.Id });

        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, Payload, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { "RememberOrders", "Inspect" }, _executions.StepExecutions.Select(s => s.NodeName));

        var inspect = _executions.StepExecutions.Last();
        var flowState = JObject.Parse(inspect.ResponsePayload)["flowState"]!;
        Assert.Equal(5, flowState["RememberOrders"]!["orders"]!.Count());
    }

    [Fact]
    public async Task CrossReferenceFilter_FailsWhenNoListNameIsConfigured()
    {
        var filter = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "SkipProcessed",
            StepType = StepType.CrossReferenceFilter,
            StepConfig = """{ "keyPaths": ["id"] }"""
        };

        var flow = new IntegrationFlow { Nodes = { filter } };
        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, Payload, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("no list name", result.ErrorMessage);
    }

    [Fact]
    public async Task ScheduleNode_PassesThroughAndActivatesDownstreamNodes()
    {
        var schedule = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "Nightly", StepType = StepType.Schedule };
        var downstream = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "Inspect", StepType = StepType.Debug };

        var flow = new IntegrationFlow { Nodes = { schedule, downstream } };
        flow.Edges.Add(new IntegrationEdge { SourceNodeId = schedule.Id, TargetNodeId = downstream.Id });

        var execution = SeedExecution(flow);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, Payload, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { "Nightly", "Inspect" }, _executions.StepExecutions.Select(s => s.NodeName));
        Assert.Equal(5, JObject.Parse(_executions.StepExecutions[0].ResponsePayload)["orders"]!.Count());
    }
}
