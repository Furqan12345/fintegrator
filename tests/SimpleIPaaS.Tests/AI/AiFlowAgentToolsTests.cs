using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

public sealed class AiFlowAgentToolsTests
{
    private static AiFlowAgentTools CreateTools(
        out StubCrossReferenceRepository crossReferences,
        out StubConnectionRepository connections,
        out StubIntegrationRepository integrations,
        out StubFlowTestRepository flowTests)
    {
        crossReferences = new StubCrossReferenceRepository();
        connections = new StubConnectionRepository();
        integrations = new StubIntegrationRepository();
        flowTests = new StubFlowTestRepository();
        return new AiFlowAgentTools(new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
                throw new InvalidOperationException("No research request expected."))),
            crossReferences, connections, integrations, flowTests, new StubExecutionRepository(), new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance);
    }

    [Fact]
    public async Task ListConnections_ReturnsMetadataOnlyAndNeverSecrets()
    {
        var tools = CreateTools(out _, out var connections, out _, out _);
        connections.Seed(new Connection
        {
            Name = "Shopify",
            BaseUrl = "https://shop.example.test/admin/api",
            AuthType = AuthType.Bearer,
            AuthConfigJson = "{\"token\":\"super-secret-value\"}"
        });
        var sources = new List<string>();

        var result = await tools.RunAsync("list_connections", "{}", sources, CancellationToken.None);

        Assert.Contains("Shopify", result, StringComparison.Ordinal);
        Assert.Contains("https://shop.example.test/admin/api", result, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", result, StringComparison.Ordinal);
        Assert.DoesNotContain("authConfigJson", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadCrossReferenceEntries_ReturnsTruncatedRealValues()
    {
        var tools = CreateTools(out var crossReferences, out _, out _, out _);
        await crossReferences.EnsureListAsync("shopify-orders", "Processed order ids");
        crossReferences.SeedKeys("shopify-orders", "order-1001");
        var sources = new List<string>();

        var result = await tools.RunAsync(
            "read_cross_reference_entries",
            JsonSerializer.Serialize(new { listName = "shopify-orders", limit = 5 }),
            sources, CancellationToken.None);

        Assert.Contains("order-1001", result, StringComparison.Ordinal);
        Assert.Contains("shopify-orders", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadTestPayloads_ReturnsStoredResponseShapes()
    {
        var tools = CreateTools(out _, out _, out var integrations, out var flowTests);
        var flowId = Guid.NewGuid();
        integrations.Seed(new IntegrationFlow { Id = flowId, Name = "Orders" });
        flowTests.Seed(new FlowTestCase
        {
            FlowId = flowId,
            Name = "Shopify order sample",
            DefinitionJson = JsonSerializer.Serialize(new
            {
                triggerPayloadJson = "{\"topic\":\"orders/create\"}",
                httpResponses = new Dictionary<string, object[]>
                {
                    ["Fetch Orders"] = new object[] { new { statusCode = 200, responseBody = "{\"orders\":[{\"id\":1001,\"admin_graphql_api_id\":\"gid\"}]}" } }
                }
            })
        });
        var sources = new List<string>();

        var result = await tools.RunAsync("read_test_payloads", "{}", sources, CancellationToken.None);

        Assert.Contains("orders", result, StringComparison.Ordinal);
        Assert.Contains("admin_graphql_api_id", result, StringComparison.Ordinal);
        Assert.Contains("Shopify order sample", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateDraft_ReportsBadPortsAndUnknownStepTypes()
    {
        var tools = CreateTools(out _, out _, out _, out _);
        var draft = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Broken draft",
                nodes = new[]
                {
                    new { id = Guid.NewGuid(), nodeName = "Trigger", stepType = "Schedule" },
                    new { id = Guid.NewGuid(), nodeName = "Work", stepType = "NotARealStepType" }
                },
                edges = new[]
                {
                    new { sourceNodeId = Guid.NewGuid(), targetNodeId = Guid.NewGuid(), sourcePortId = "up", targetPortId = "down" }
                }
            }
        });
        var sources = new List<string>();

        var result = await tools.RunAsync("validate_draft", JsonSerializer.Serialize(new { draftJson = draft }), sources, CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("valid").GetBoolean());
        var errors = string.Join(" ", document.RootElement.GetProperty("errors").EnumerateArray().Select(error => error.GetString()));
        Assert.Contains("source node that does not exist", errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateDraft_AcceptsAWellFormedDraft()
    {
        var tools = CreateTools(out _, out _, out _, out _);
        var triggerId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var draft = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Shopify orders",
                nodes = new object[]
                {
                    new { id = triggerId, nodeName = "Trigger", stepType = "Schedule", stepConfig = "{\"schedule\":{\"mode\":\"Cron\",\"cronExpression\":\"0 * * * *\"}}" },
                    new { id = storeId, nodeName = "Store orders", stepType = "CrossReferenceStore", stepConfig = "{\"listName\":\"shopify-orders\",\"arrayPath\":\"flowState.Fetch.orders\",\"keyPaths\":[\"id\"]}" }
                },
                edges = new object[]
                {
                    new { sourceNodeId = triggerId, targetNodeId = storeId, sourcePortId = "right", targetPortId = "left" }
                }
            }
        });
        var sources = new List<string>();

        var result = await tools.RunAsync("validate_draft", JsonSerializer.Serialize(new { draftJson = draft }), sources, CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task ReadFlow_HidesConnectionSecrets()
    {
        var tools = CreateTools(out _, out _, out var integrations, out _);
        var flowId = Guid.NewGuid();
        integrations.Seed(new IntegrationFlow
        {
            Id = flowId,
            Name = "Existing",
            Nodes =
            {
                new IntegrationStep
                {
                    Id = Guid.NewGuid(),
                    NodeName = "Call API",
                    StepType = StepType.HttpAction,
                    AuthToken = "stored-auth-token",
                    AuthConfigJson = "{\"clientSecret\":\"stored-client-secret\"}"
                }
            }
        });
        var sources = new List<string>();

        var result = await tools.RunAsync("read_flow", JsonSerializer.Serialize(new { flowId }), sources, CancellationToken.None);

        Assert.Contains("Call API", result, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-auth-token", result, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-client-secret", result, StringComparison.Ordinal);
    }

    private static AiFlowAgentTools CreateTools(StubExecutionRepository executions) =>
        new(new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
                throw new InvalidOperationException("No research request expected."))),
            new StubCrossReferenceRepository(), new StubConnectionRepository(),
            new StubIntegrationRepository(), new StubFlowTestRepository(), executions, new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance);

    [Fact]
    public async Task ListRecentExecutions_ReportsStatusErrorsAndIds()
    {
        var executions = new StubExecutionRepository();
        var executionId = Guid.NewGuid();
        executions.Seed(new FlowExecution
        {
            Id = executionId,
            FlowId = Guid.NewGuid(),
            FlowName = "Shopify orders",
            Status = ExecutionStatus.Failed,
            ErrorMessage = "The SSL connection could not be established",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        });
        var tools = CreateTools(executions);
        var sources = new List<string>();

        var result = await tools.RunAsync("list_recent_executions", "{}", sources, CancellationToken.None);

        Assert.Contains(executionId.ToString(), result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Shopify orders", result, StringComparison.Ordinal);
        Assert.Contains("Failed", result, StringComparison.Ordinal);
        Assert.Contains("SSL", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadExecution_ReportsPerNodeFailures()
    {
        var executions = new StubExecutionRepository();
        var executionId = Guid.NewGuid();
        executions.Seed(new FlowExecution
        {
            Id = executionId,
            FlowId = Guid.NewGuid(),
            FlowName = "Shopify orders",
            Status = ExecutionStatus.Failed
        });
        executions.StepExecutions.Add(new StepExecution
        {
            FlowExecutionId = executionId,
            StepId = Guid.NewGuid(),
            NodeName = "Fetch orders",
            Status = ExecutionStatus.Failed,
            ErrorMessage = "HTTP 401 unauthorized",
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        var tools = CreateTools(executions);
        var sources = new List<string>();

        var result = await tools.RunAsync("read_execution", JsonSerializer.Serialize(new { executionId }), sources, CancellationToken.None);

        Assert.Contains("Fetch orders", result, StringComparison.Ordinal);
        Assert.Contains("401", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListDeadLetters_ReportsRetryState()
    {
        var executions = new StubExecutionRepository();
        executions.DeadLetters.Add(new DeadLetterEntry
        {
            FlowName = "Shopify orders",
            NodeName = "Store orders",
            Status = "Pending",
            RetryCount = 2,
            ErrorMessage = "listName is missing"
        });
        var tools = CreateTools(executions);
        var sources = new List<string>();

        var result = await tools.RunAsync("list_dead_letters", "{}", sources, CancellationToken.None);

        Assert.Contains("Store orders", result, StringComparison.Ordinal);
        Assert.Contains("Pending", result, StringComparison.Ordinal);
        Assert.Contains("listName is missing", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateDraft_StopsEndlessValidationLoopsAfterSixAttempts()
    {
        var tools = CreateTools(out _, out _, out _, out _);
        var badDraft = JsonSerializer.Serialize(new { flow = new { name = "", nodes = Array.Empty<object>(), edges = Array.Empty<object>() } });
        var sources = new List<string>();
        var result = string.Empty;

        for (var attempt = 0; attempt < 7; attempt++)
            result = await tools.RunAsync("validate_draft", JsonSerializer.Serialize(new { draftJson = badDraft }), sources, CancellationToken.None);

        Assert.Contains("Stop calling this tool", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadStepPackets_ShowsActualRequestAndResponseBodies()
    {
        var executions = new StubExecutionRepository();
        var executionId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        executions.Seed(new FlowExecution
        {
            Id = executionId,
            FlowId = Guid.NewGuid(),
            FlowName = "Shopify orders",
            Status = ExecutionStatus.Failed
        });
        executions.StepExecutions.Add(new StepExecution
        {
            Id = stepId,
            FlowExecutionId = executionId,
            StepId = stepId,
            NodeName = "FetchShopifyOrders",
            Status = ExecutionStatus.Failed,
            ErrorMessage = "Pagination on node FetchShopifyOrders repeated the same continuation value.",
            StartedAt = DateTime.UtcNow
        });
        executions.StepPacketLogs.Add(new StepPacketLog
        {
            StepExecutionId = stepId,
            Sequence = 1,
            Kind = "HTTP",
            PageNumber = 1,
            HttpMethod = "POST",
            RequestUrl = "https://shop.example.test/graphql.json",
            StatusCode = 200,
            RequestBody = "{\"variables\":{\"cursor\":null}}",
            ResponseBody = "{\"pageInfo\":{\"endCursor\":\"cursor-1\"}}",
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        });
        var tools = CreateTools(executions);
        var sources = new List<string>();

        var result = await tools.RunAsync("read_step_packets",
            JsonSerializer.Serialize(new { executionId, nodeName = "FetchShopifyOrders" }), sources, CancellationToken.None);

        Assert.Contains("FetchShopifyOrders", result, StringComparison.Ordinal);
        Assert.Contains("cursor", result, StringComparison.Ordinal);
        Assert.Contains("cursor-1", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListIntegrations_ReturnsGroups()
    {
        var catalog = new StubIntegrationCatalogRepository();
        catalog.Seed(new Integration { Name = "Shopify sync", Description = "Order replication" });
        var tools = CreateToolsWith(catalog: catalog);

        var result = await tools.RunAsync("list_integrations", "{}", new List<string>(), CancellationToken.None);

        Assert.Contains("Shopify sync", result, StringComparison.Ordinal);
        Assert.Contains("Order replication", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchLogs_FindsErrorsByTextAndLevel()
    {
        var logs = new StubLogRepository();
        logs.Entries.Add(new AppLogEntry { Level = "Error", EventName = "flow.execution.failed", Message = "SSL connection could not be established", Timestamp = DateTime.UtcNow });
        logs.Entries.Add(new AppLogEntry { Level = "Information", EventName = "log", Message = "Flow saved", Timestamp = DateTime.UtcNow });
        var tools = CreateToolsWith(logs: logs);

        var result = await tools.RunAsync("search_logs",
            JsonSerializer.Serialize(new { search = "SSL", level = "Error" }), new List<string>(), CancellationToken.None);

        Assert.Contains("SSL connection", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Flow saved", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDeadLetter_ReturnsFailedPayloadAndState()
    {
        var executions = new StubExecutionRepository();
        executions.DeadLetters.Add(new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowName = "Shopify orders",
            NodeName = "Store orders",
            Status = "Pending",
            Payload = "{\"id\":\"order-1\"}",
            FlowStateJson = "{\"Trigger\":{\"id\":\"order-1\"}}",
            AttemptHistoryJson = "[{\"error\":\"boom\"}]",
            ErrorMessage = "listName is missing"
        });
        var tools = CreateToolsWith(executions: executions);
        var deadLetterId = executions.DeadLetters[0].Id;

        var result = await tools.RunAsync("read_dead_letter",
            JsonSerializer.Serialize(new { deadLetterId }), new List<string>(), CancellationToken.None);

        Assert.Contains("order-1", result, StringComparison.Ordinal);
        Assert.Contains("AttemptHistory", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("listName is missing", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFlowTests_ReportsLatestRunResults()
    {
        var flowTests = new StubFlowTestRepository();
        var flowId = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        flowTests.Seed(new FlowTestCase
        {
            Id = caseId,
            FlowId = flowId,
            Name = "Order happy path",
            Description = "One order, one cross-reference",
            RequiredForPublish = true
        });
        await flowTests.AddRunAsync(new FlowTestRun
        {
            TestCaseId = caseId,
            FlowId = flowId,
            Status = "Failed",
            ResultJson = "{\"assertions\":[{\"ok\":false}]}",
            StartedAt = DateTime.UtcNow
        });
        var tools = CreateToolsWith(flowTests: flowTests);

        var result = await tools.RunAsync("list_flow_tests",
            JsonSerializer.Serialize(new { flowId }), new List<string>(), CancellationToken.None);

        Assert.Contains("Order happy path", result, StringComparison.Ordinal);
        Assert.Contains("Failed", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFlowVersions_MarksCurrentPublishedVersion()
    {
        var integrations = new StubIntegrationRepository();
        var flowId = Guid.NewGuid();
        integrations.Seed(new IntegrationFlow { Id = flowId, Name = "Shopify orders", PublishedVersion = 2 });
        integrations.Versions.Add(new FlowVersion { FlowId = flowId, VersionNumber = 1, ChangeNote = "First publish" });
        integrations.Versions.Add(new FlowVersion { FlowId = flowId, VersionNumber = 2, ChangeNote = "Added dedupe" });
        var tools = CreateToolsWith(integrations: integrations);

        var result = await tools.RunAsync("list_flow_versions",
            JsonSerializer.Serialize(new { flowId }), new List<string>(), CancellationToken.None);

        Assert.Contains("Added dedupe", result, StringComparison.Ordinal);
        Assert.Contains("\"isCurrent\":true", result.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Contains("\"publishedVersion\":2", result.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadStepPayload_ReturnsNodeInputAndOutput()
    {
        var executions = new StubExecutionRepository();
        var executionId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        executions.Seed(new FlowExecution { Id = executionId, FlowId = Guid.NewGuid(), FlowName = "Shopify orders", Status = ExecutionStatus.Success });
        executions.StepExecutions.Add(new StepExecution
        {
            Id = stepId,
            FlowExecutionId = executionId,
            StepId = stepId,
            NodeName = "FetchShopifyOrders",
            Status = ExecutionStatus.Success,
            ReceivedInput = "{\"trigger\":{\"id\":\"order-1\"}}",
            RequestPayload = "{\"query\":\"{ orders }\"}",
            ResponsePayload = "{\"data\":{\"orders\":{\"edges\":[{\"node\":{\"id\":\"gid://Order/1\"}}]}}}",
            StartedAt = DateTime.UtcNow
        });
        var tools = CreateToolsWith(executions: executions);

        var result = await tools.RunAsync("read_step_payload",
            JsonSerializer.Serialize(new { executionId, nodeName = "FetchShopifyOrders" }), new List<string>(), CancellationToken.None);

        Assert.Contains("gid://Order/1", result, StringComparison.Ordinal);
        Assert.Contains("order-1", result, StringComparison.Ordinal);
    }

    private static AiFlowAgentTools CreateToolsWith(
        StubExecutionRepository? executions = null,
        StubIntegrationCatalogRepository? catalog = null,
        StubLogRepository? logs = null,
        StubIntegrationRepository? integrations = null,
        StubFlowTestRepository? flowTests = null) =>
        new(new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
                throw new InvalidOperationException("No research request expected."))),
            new StubCrossReferenceRepository(), new StubConnectionRepository(),
            integrations ?? new StubIntegrationRepository(), flowTests ?? new StubFlowTestRepository(),
            executions ?? new StubExecutionRepository(), catalog ?? new StubIntegrationCatalogRepository(),
            logs ?? new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance);
}
