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
    public void ResolvePaths_ReturnsSingleTokenForATopLevelField()
    {
        var input = JObject.Parse("""{ "id": 42 }""");
        var results = CrossReferenceKeyBuilder.ResolvePaths(input, "id").ToList();
        Assert.Single(results);
        Assert.Equal(42, results[0]!.Value<int>());
    }

    [Fact]
    public void ResolvePaths_ExpandsAWildcardArraySegment()
    {
        var input = JObject.Parse("""{ "orders": [ { "id": "1" }, { "id": "2" } ] }""");
        var results = CrossReferenceKeyBuilder.ResolvePaths(input, "orders[*]").ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal("1", results[0]!["id"]!.ToString());
        Assert.Equal("2", results[1]!["id"]!.ToString());
    }

    [Fact]
    public void ResolvePaths_ExpandsNestedWildcards()
    {
        var input = JObject.Parse("""
        { "orders": [
            { "id": "1", "items": [{ "sku": "A" }, { "sku": "B" }] },
            { "id": "2", "items": [{ "sku": "C" }] }
        ] }
        """);
        var results = CrossReferenceKeyBuilder.ResolvePaths(input, "orders[*].items[*]").ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal("A", results[0]!["sku"]!.ToString());
        Assert.Equal("B", results[1]!["sku"]!.ToString());
        Assert.Equal("C", results[2]!["sku"]!.ToString());
    }

    [Fact]
    public void ContainsWildcard_DetectsAsteriskInPath()
    {
        Assert.True(CrossReferenceKeyBuilder.ContainsWildcard("orders[*]"));
        Assert.True(CrossReferenceKeyBuilder.ContainsWildcard("orders[*].items[*]"));
        Assert.False(CrossReferenceKeyBuilder.ContainsWildcard("orders"));
        Assert.False(CrossReferenceKeyBuilder.ContainsWildcard("orders.0"));
    }

    [Fact]
    public void FilterArrayPreservingStructure_FiltersNestedArrayElements()
    {
        var input = JObject.Parse("""
        { "orders": [
            { "id": "1", "items": [
                { "sku": "A", "qty": 1 },
                { "sku": "B", "qty": 2 }
            ] },
            { "id": "2", "items": [
                { "sku": "C", "qty": 3 }
            ] }
        ] }
        """);

        var knownKeys = new HashSet<string>(new[] { "A", "C" }, StringComparer.Ordinal);
        var result = CrossReferenceKeyBuilder.FilterArrayPreservingStructure(
            input, "orders[*].items[*]", knownKeys, new[] { "sku" });

        var orders = (JArray)result!["orders"]!;
        Assert.NotNull(orders);
        Assert.Equal(2, orders.Count);
        Assert.Single((JArray)orders[0]!["items"]!);
        Assert.Equal("B", orders[0]!["items"]![0]!["sku"]!.ToString());
        Assert.Empty((JArray)orders[1]!["items"]!);
    }

    [Fact]
    public void FilterArrayPreservingStructure_PreservesParentObjectFields()
    {
        var input = JObject.Parse("""
        { "orders": [
            { "id": "1", "status": "new", "items": [
                { "sku": "A" }
            ] }
        ] }
        """);

        var knownKeys = new HashSet<string>(StringComparer.Ordinal);
        var result = CrossReferenceKeyBuilder.FilterArrayPreservingStructure(
            input, "orders[*].items[*]", knownKeys, new[] { "sku" });

        Assert.NotNull(result!["orders"]![0]!["id"]);
        Assert.Equal("1", result!["orders"]![0]!["id"]!.ToString());
        Assert.Equal("new", result!["orders"]![0]!["status"]!.ToString());
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

    [Fact]
    public async Task CrossReferenceStore_ResolvesAFlowStatePrefixedPathAgainstTheWholeTree()
    {
        var wrappedPayload = """
            { "processedData": [
                { "id": "1", "status": "new" },
                { "id": "2", "status": "new" },
                { "id": "3", "status": "new" }
            ] }
            """;

        var store = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "RememberOrders",
            StepType = StepType.CrossReferenceStore,
            StepConfig = """
                { "listName": "processed-orders", "arrayPath": "flowState.trigger.processedData",
                  "keyPaths": ["id"], "valuePaths": ["status"] }
                """
        };

        var flow = new IntegrationFlow { Nodes = { store } };
        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, wrappedPayload, CancellationToken.None);

        Assert.Equal(3, _crossReferences.Entries.Count);
        Assert.Equal(new[] { "1", "2", "3" }, _crossReferences.Entries.Select(e => e.KeyValue));
    }

    [Fact]
    public async Task CrossReferenceStore_ResolvesNestedArrayWildcardForKeys()
    {
        var payload = """
        { "orders": [
            { "id": "1", "items": [
                { "sku": "A", "qty": 1 },
                { "sku": "B", "qty": 2 }
            ] },
            { "id": "2", "items": [
                { "sku": "C", "qty": 3 }
            ] }
        ] }
        """;

        var store = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "RememberItems",
            StepType = StepType.CrossReferenceStore,
            StepConfig = """
                { "listName": "processed-orders", "arrayPath": "orders[*].items[*]",
                  "keyPaths": ["sku"], "valuePaths": ["qty"] }
                """
        };

        var flow = new IntegrationFlow { Nodes = { store } };
        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, payload, CancellationToken.None);

        Assert.Equal(3, _crossReferences.Entries.Count);
        Assert.Equal(new[] { "A", "B", "C" }, _crossReferences.Entries.Select(e => e.KeyValue));
    }

    [Fact]
    public async Task CrossReferenceFilter_PreservesStructureWithNestedWildcardPath()
    {
        var payload = """
        { "orders": [
            { "id": "1", "items": [
                { "sku": "A", "qty": 1 },
                { "sku": "B", "qty": 2 }
            ] },
            { "id": "2", "items": [
                { "sku": "C", "qty": 3 }
            ] }
        ] }
        """;

        _crossReferences.SeedKeys(ListName, "A", "C");

        var filter = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "SkipProcessed",
            StepType = StepType.CrossReferenceFilter,
            StepConfig = """
                { "listName": "processed-orders", "arrayPath": "orders[*].items[*]",
                  "keyPaths": ["sku"] }
                """
        };

        var flow = new IntegrationFlow { Nodes = { filter } };
        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, payload, CancellationToken.None);

        var step = Assert.Single(_executions.StepExecutions);
        var result = JObject.Parse(step.ResponsePayload);

        // Structure is preserved — original object shape
        var orders = (JArray)result["orders"]!;
        Assert.NotNull(orders);
        Assert.Equal(2, orders.Count);

        // order 1: sku A is known (filtered out), sku B is kept
        Assert.Single((JArray)orders[0]!["items"]!);
        Assert.Equal("B", orders[0]!["items"]![0]!["sku"]!.ToString());

        // order 2: sku C is known (filtered out), items empty
        Assert.Empty((JArray)orders[1]!["items"]!);
    }

    [Fact]
    public async Task CrossReferenceFilter_WithoutWildcardEmitsFlatArrayForBackwardCompatibility()
    {
        _crossReferences.SeedKeys(ListName, "1", "2");

        var filter = Node("SkipProcessed", StepType.CrossReferenceFilter);
        var flow = new IntegrationFlow { Nodes = { filter } };
        var execution = SeedExecution(flow);

        await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, Payload, CancellationToken.None);

        var step = Assert.Single(_executions.StepExecutions);
        var emitted = JArray.Parse(step.ResponsePayload);

        Assert.Equal(3, emitted.Count);
        Assert.Equal(new[] { "3", "4", "5" }, emitted.Select(record => record["id"]!.ToString()));
    }
}
