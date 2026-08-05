using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

/// <summary>
/// Executes the sample integration shipped under samples/integrations and asserts
/// that both the ForEach N+1 fan-out and the nested cross-reference filter behave as documented.
/// </summary>
public class SampleIntegrationTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();
    private readonly StubCrossReferenceRepository _crossReferences = new();

    private static AdvancedCodeExecutionService CreateCodeService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scripting:TimeoutSeconds"] = "60"
            })
            .Build();

        return new AdvancedCodeExecutionService(configuration);
    }

    private static string ResolveSamplePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir.Parent != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SimpleIPaaS.slnx")))
            {
                return Path.Combine(dir.FullName, "samples", "integrations", "sp-api-orders-nplus1.json");
            }

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "integrations", "sp-api-orders-nplus1.json");
    }

    private static IntegrationFlowDto LoadSample()
    {
        var json = File.ReadAllText(ResolveSamplePath());
        var dto = System.Text.Json.JsonSerializer.Deserialize<IntegrationFlowDto>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Sample flow failed to deserialize.");

        dto.Id = Guid.Empty; // let the server mint a fresh flow id on import
        return dto;
    }

    private sealed class RecordingTransportEngine : ITransportEngine
    {
        private readonly Queue<(int StatusCode, string Response)> _responses = new();

        public List<string> RequestedUrls { get; } = new();
        public List<string> RequestPayloads { get; } = new();

        public void Enqueue(int statusCode, string response) => _responses.Enqueue((statusCode, response));

        public Task<(int StatusCode, string Response)> DispatchAsync(
            IntegrationStep step, string? payload, Guid? connectionId = null,
            CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(step.EndpointUrl);
            RequestPayloads.Add(payload ?? string.Empty);
            var response = _responses.Count > 0 ? _responses.Dequeue() : (200, "{}");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SampleFlow_FanOutPerOrderAndDeduplicatesKnownSKUs()
    {
        // Arrange — load the shipped sample and assert it is structurally valid.
        var dto = LoadSample();
        var validationErrors = FlowValidator.Validate(dto);
        Assert.Empty(validationErrors);

        var flow = dto.ToEntity();
        flow.TenantId = Guid.NewGuid();

        _flows.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TenantId = flow.TenantId, TriggerSource = "Test" };
        _executions.Seed(execution);

        // Simulate a previous night's run: SKU-A1 was already processed and is therefore known.
        _crossReferences.SeedKeys("processed-skus", "SKU-A1");

        var transport = new RecordingTransportEngine();

        // getOrders response (wrapped SP-API envelope).
        transport.Enqueue(200, """
        { "payload": { "Orders": [
            { "AmazonOrderId": "A111-123", "OrderStatus": "Shipped" },
            { "AmazonOrderId": "A222-456", "OrderStatus": "Unshipped" }
        ] } }
        """);

        // Per-order orderItems — order A111 carries a repeat (SKU-A1) + a new SKU-B1.
        transport.Enqueue(200, """
        { "payload": { "OrderItems": [
            { "sku": "SKU-A1", "asin": "ASIN-1", "qty": 1 },
            { "sku": "SKU-B1", "asin": "ASIN-2", "qty": 2 }
        ] } }
        """);

        // Order A222 carries the repeat SKU-A1 plus another new SKU-C1.
        transport.Enqueue(200, """
        { "payload": { "OrderItems": [
            { "sku": "SKU-A1", "asin": "ASIN-1", "qty": 1 },
            { "sku": "SKU-C1", "asin": "ASIN-3", "qty": 3 }
        ] } }
        """);

        transport.Enqueue(200, "{}"); // ProcessNewItems POST succeeds

        var executor = new FlowExecutor(
            _flows,
            transport,
            CreateCodeService(),
            _executions,
            _crossReferences,
            NullLogger<FlowExecutor>.Instance);

        // Act
        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        // Assert — the whole flow completed.
        Assert.Equal(ExecutionStatus.Success, result.Status);

        // ForEach N+1 fan-out: getOrderItems was called once per order, the URL carries each AmazonOrderId.
        var lineItemUrls = transport.RequestedUrls
            .Where(u => u.Contains("/orderItems"))
            .ToArray();
        Assert.Equal(2, lineItemUrls.Length);
        Assert.Contains(lineItemUrls, u => u.EndsWith("/A111-123/orderItems"));
        Assert.Contains(lineItemUrls, u => u.EndsWith("/A222-456/orderItems"));

        // Nested cross-reference filter: structure preserved, SKU-A1 (already known) removed at the item level.
        var filterStep = _executions.StepExecutions.First(s => s.NodeName == "FilterNewSKUs");
        var filtered = JObject.Parse(filterStep.ResponsePayload);

        var orders = (JArray)filtered["orders"]!;
        Assert.Equal(2, orders.Count);

        var order1Items = (JArray)orders[0]!["items"]!;
        var order2Items = (JArray)orders[1]!["items"]!;
        Assert.Single(order1Items);
        Assert.Equal("SKU-B1", order1Items[0]!["sku"]!.ToString());
        Assert.Single(order2Items);
        Assert.Equal("SKU-C1", order2Items[0]!["sku"]!.ToString());

        // The store recorded only the genuinely new SKUs this run (SKU-A1 was already seeded).
        Assert.Equal(3, _crossReferences.Entries.Count); // SKU-A1 (seeded) + SKU-B1 + SKU-C1
        Assert.Contains("SKU-B1", _crossReferences.Entries.Select(e => e.KeyValue));
        Assert.Contains("SKU-C1", _crossReferences.Entries.Select(e => e.KeyValue));

        // The downstream POST received only the new items — SKU-A1 is absent from the body.
        var postPayload = transport.RequestPayloads
            .FirstOrDefault(p => p.Contains("SKU-B1") || p.Contains("SKU-C1"));
        Assert.NotNull(postPayload);
        Assert.Contains("SKU-B1", postPayload!);
        Assert.Contains("SKU-C1", postPayload!);
        Assert.DoesNotContain("SKU-A1", postPayload!);
    }
}
