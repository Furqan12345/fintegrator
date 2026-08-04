using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System.Text.Json.Nodes;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public class ForEachTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();
    private readonly StubCrossReferenceRepository _crossReferences = new();

    private readonly string[] OrderPayload =
    [
        """{ "orders": [ { "id": 1, "AmazonOrderId": "A111" }, { "id": 2, "AmazonOrderId": "A222" } ] }""",
    ];

    private FlowExecutor CreateExecutor(ITransportEngine transport, IAdvancedCodeExecutionService codeExec) => new(
        _flows,
        transport,
        codeExec,
        _executions,
        _crossReferences,
        NullLogger<FlowExecutor>.Instance);

    private FlowExecutor CreateExecutor(ITransportEngine transport) => new(
        _flows,
        transport,
        new UnreachableCodeExecutionService(),
        _executions,
        _crossReferences,
        NullLogger<FlowExecutor>.Instance);

    private static AdvancedCodeExecutionService CreateCodeService(int timeoutSeconds = 60)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scripting:TimeoutSeconds"] = timeoutSeconds.ToString()
            })
            .Build();

        return new AdvancedCodeExecutionService(configuration);
    }

    private static IntegrationStep ForEachNode(string name, string arrayPath, string itemVariable = "item") => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.ForEach,
        StepConfig = new JsonObject
        {
            ["forEach"] = new JsonObject
            {
                ["arrayPath"] = arrayPath,
                ["itemVariable"] = itemVariable
            }
        }.ToJsonString(JsonSerializerOptions)
    };

    private static IntegrationStep DebugNode(string name) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.Debug
    };

    private static IntegrationStep HttpNode(string name, string url) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.HttpAction,
        HttpMethod = "GET",
        EndpointUrl = url
    };

    private static IntegrationStep ScheduleNode(string name) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.Schedule
    };

    private static IntegrationEdge Edge(IntegrationStep source, IntegrationStep target) => new()
    {
        SourceNodeId = source.Id,
        TargetNodeId = target.Id
    };

    private FlowExecution SeedExecution(IntegrationFlow flow)
    {
        _flows.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TenantId = flow.TenantId, TriggerSource = "Test" };
        _executions.Seed(execution);
        return execution;
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { WriteIndented = false };

    private sealed class RecordingTransportEngine : ITransportEngine
    {
        private readonly Queue<(int StatusCode, string Response)> _responses = new();

        public List<string> RequestedUrls { get; } = new();

        public void Enqueue(int statusCode, string response) => _responses.Enqueue((statusCode, response));

        public Task<(int StatusCode, string Response)> DispatchAsync(
            IntegrationStep step, string? payload, Guid? connectionId = null,
            CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(step.EndpointUrl);
            var response = _responses.Count > 0 ? _responses.Dequeue() : (200, "{}");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ForEach_FansOutArrayAndCombinesResults()
    {
        var forEach = ForEachNode("ForOrders", "orders");
        var inspector = DebugNode("ItemInspector");

        var flow = new IntegrationFlow
        {
            Nodes = { forEach, inspector },
            Edges = { Edge(forEach, inspector) }
        };
        flow.Nodes.Add(inspector);

        var execution = SeedExecution(flow);

        var result = await CreateExecutor(new UnreachableTransportEngine())
            .ExecuteFlowAsync(flow.Id, execution.Id, OrderPayload[0], CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(3, result.TotalRecords);     // 1 ForEach + 2 Debug
        Assert.Equal(3, result.SuccessRecords);

        var stepExecutions = _executions.StepExecutions;
        Assert.Equal(new[] { "ForOrders", "ItemInspector", "ItemInspector" },
            stepExecutions.Select(s => s.NodeName));

        var forEachOutput = JArray.Parse(stepExecutions.First(s => s.NodeName == "ForOrders").ResponsePayload);
        Assert.Equal(2, forEachOutput.Count);
    }

    [Fact]
    public async Task ForEach_WithEmptyArrayProducesEmptyOutput()
    {
        var forEach = ForEachNode("ForOrders", "orders");
        var inspector = DebugNode("ItemInspector");

        var flow = new IntegrationFlow
        {
            Nodes = { forEach, inspector },
            Edges = { Edge(forEach, inspector) }
        };

        var execution = SeedExecution(flow);

        var result = await CreateExecutor(new UnreachableTransportEngine())
            .ExecuteFlowAsync(flow.Id, execution.Id, """{"orders":[]}""", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(1, result.TotalRecords);   // only ForEach runs, no subgraph iterations

        var step = Assert.Single(_executions.StepExecutions);
        Assert.Equal("ForOrders", step.NodeName);
        Assert.Equal("[]", step.ResponsePayload);
    }

    [Fact]
    public async Task ForEach_PassesItemIntoUrlCode()
    {
        var forEach = ForEachNode("ForOrders", "orders");
        var itemsNode = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "FetchItems",
            StepType = StepType.HttpAction,
            HttpMethod = "GET",
            UrlCode = "var fs = JObject.Parse(FlowStateJson); return \"https://example.test/items/\" + fs[\"item\"][\"AmazonOrderId\"];"
        };

        var flow = new IntegrationFlow
        {
            Nodes = { forEach, itemsNode },
            Edges = { Edge(forEach, itemsNode) }
        };

        var execution = SeedExecution(flow);

        var transport = new RecordingTransportEngine();
        transport.Enqueue(200, """{"items":[{"sku":"A"}]}""");
        transport.Enqueue(200, """{"items":[{"sku":"B"}]}""");
        transport.Enqueue(200, """{"items":[{"sku":"C"}]}""");

        var payload = """{ "orders": [ { "AmazonOrderId": "A111" }, { "AmazonOrderId": "A222" }, { "AmazonOrderId": "A333" } ] }""";

        var result = await CreateExecutor(transport, CreateCodeService())
            .ExecuteFlowAsync(flow.Id, execution.Id, payload, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(4, result.TotalRecords);  // 1 ForEach + 3 HTTP

        Assert.Equal(3, transport.RequestedUrls.Count);
        Assert.Contains("https://example.test/items/A111", transport.RequestedUrls);
        Assert.Contains("https://example.test/items/A222", transport.RequestedUrls);
        Assert.Contains("https://example.test/items/A333", transport.RequestedUrls);
    }

    [Fact]
    public async Task ForEach_ActivatesMergeNodeWithCombinedOutputInFlowState()
    {
        var start = ScheduleNode("Nightly");
        var forEach = ForEachNode("ForOrders", "orders");
        var itemProcessor = DebugNode("ItemProcessor");
        var otherNode = DebugNode("OtherPath");
        var merge = DebugNode("Merge");

        var flow = new IntegrationFlow
        {
            Nodes = { start, forEach, itemProcessor, otherNode, merge },
            Edges =
            {
                Edge(start, forEach),
                Edge(start, otherNode),
                Edge(forEach, itemProcessor),
                Edge(itemProcessor, merge),
                Edge(otherNode, merge)
            }
        };

        var execution = SeedExecution(flow);

        var result = await CreateExecutor(new UnreachableTransportEngine())
            .ExecuteFlowAsync(flow.Id, execution.Id,
                """{ "orders": [ { "id": 1 }, { "id": 2 } ] }""", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);

        // ForEach, ItemProcessor (x2), OtherPath, Merge = 6 steps
        Assert.Equal(6, result.TotalRecords);

        var mergeStep = _executions.StepExecutions.Last(s => s.NodeName == "Merge");
        var debugDump = JObject.Parse(mergeStep.ResponsePayload);
        var flowState = debugDump["flowState"] as JObject;

        // The combined array is stored under the ForEach node's name
        var combined = flowState?["ForOrders"] as JArray;
        Assert.NotNull(combined);
        Assert.Equal(2, combined!.Count);
    }

    [Fact]
    public async Task ForEach_WithNoSubgraphNodesEmitsArrayElementsDirectly()
    {
        var forEach = ForEachNode("EchoOrders", "orders");

        var flow = new IntegrationFlow
        {
            Nodes = { forEach },
            Edges = { }
        };

        var execution = SeedExecution(flow);

        var result = await CreateExecutor(new UnreachableTransportEngine())
            .ExecuteFlowAsync(flow.Id, execution.Id,
                """{ "orders": [ { "id": 1 }, { "id": 2 }, { "id": 3 } ] }""", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);

        // ForEach (no subgraph) = 1 step execution
        Assert.Equal(1, result.TotalRecords);

        var forEachStep = _executions.StepExecutions.First(s => s.NodeName == "EchoOrders");
        var combined = JArray.Parse(forEachStep.ResponsePayload);
        Assert.Equal(3, combined.Count);
    }
}
