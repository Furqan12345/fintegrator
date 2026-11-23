using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public class PaginationTests
{
    [Fact]
    public async Task HttpAction_AmazonNextTokenAggregatesPagesAndPreservesQuery()
    {
        var transport = new RecordingPaginationTransport(
            new TransportResponse(200, "{\"payload\":{\"Orders\":[{\"id\":1}],\"NextToken\":\"next-1\"}}", new Dictionary<string, string[]>(), "https://api.example.test/orders"),
            new TransportResponse(200, "{\"payload\":{\"Orders\":[{\"id\":2}]}}", new Dictionary<string, string[]>(), "https://api.example.test/orders"));
        var flowRepository = new StubIntegrationRepository();
        var executionRepository = new StubExecutionRepository();
        var crossReferenceRepository = new StubCrossReferenceRepository();
        var flow = new IntegrationFlow
        {
            Nodes =
            {
                new IntegrationStep
                {
                    Id = Guid.NewGuid(),
                    NodeName = "FetchOrders",
                    StepType = StepType.HttpAction,
                    HttpMethod = "GET",
                    EndpointUrl = "https://api.example.test/orders?MarketplaceIds=ATVPDKIKX0DER",
                    StepConfig = "{\"pagination\":{\"style\":\"AmazonNextToken\",\"maxPages\":3,\"aggregatePath\":\"payload.Orders\",\"nextTokenPath\":\"payload.NextToken\",\"nextTokenParameter\":\"NextToken\"}}"
                }
            }
        };
        flowRepository.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        executionRepository.Seed(execution);
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, crossReferenceRepository, new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(2, transport.RequestedUrls.Count);
        Assert.Contains("MarketplaceIds=ATVPDKIKX0DER", transport.RequestedUrls[0]);
        Assert.Contains("MarketplaceIds=ATVPDKIKX0DER&NextToken=next-1", transport.RequestedUrls[1]);
        Assert.Contains("\"id\":1", executionRepository.StepExecutions.Single().ResponsePayload);
        Assert.Contains("\"id\":2", executionRepository.StepExecutions.Single().ResponsePayload);
        Assert.Contains("{}", executionRepository.StepExecutions.Single().ReceivedInput);
        Assert.Equal(2, executionRepository.StepPacketLogs.Count);
        Assert.All(executionRepository.StepPacketLogs, packet => Assert.Equal("HTTP", packet.Kind));
    }

    [Fact]
    public async Task HttpAction_StopsCleanlyAtConfiguredPageLimit()
    {
        var transport = new RecordingPaginationTransport(
            new TransportResponse(200, "{\"payload\":{\"Orders\":[{\"id\":1}],\"NextToken\":\"next-1\"}}", new Dictionary<string, string[]>(), "https://api.example.test/orders"));
        var flowRepository = new StubIntegrationRepository();
        var executionRepository = new StubExecutionRepository();
        var flow = new IntegrationFlow
        {
            Nodes =
            {
                new IntegrationStep
                {
                    Id = Guid.NewGuid(),
                    NodeName = "FetchOrders",
                    StepType = StepType.HttpAction,
                    HttpMethod = "GET",
                    EndpointUrl = "https://api.example.test/orders",
                    StepConfig = "{\"pagination\":{\"style\":\"AmazonNextToken\",\"maxPages\":1,\"aggregatePath\":\"payload.Orders\",\"nextTokenPath\":\"payload.NextToken\",\"nextTokenParameter\":\"NextToken\"}}"
                }
            }
        };
        flowRepository.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        executionRepository.Seed(execution);
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, new StubCrossReferenceRepository(), new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        var payload = executionRepository.StepExecutions.Single().ResponsePayload;
        Assert.Contains("\"id\":1", payload);
        Assert.DoesNotContain("\"id\":2", payload);
    }

    [Fact]
    public async Task HttpAction_CursorInjectsContinuationIntoJsonBodyPath()
    {
        var transport = new RecordingPaginationTransport(
            new TransportResponse(200, "{\"data\":{\"orders\":{\"edges\":[{\"node\":{\"id\":1}}],\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"cursor-1\"}}}}", new Dictionary<string, string[]>(), "https://shop.example.test/graphql.json"),
            new TransportResponse(200, "{\"data\":{\"orders\":{\"edges\":[{\"node\":{\"id\":2}}],\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null}}}}", new Dictionary<string, string[]>(), "https://shop.example.test/graphql.json"));
        var flowRepository = new StubIntegrationRepository();
        var executionRepository = new StubExecutionRepository();
        var flow = new IntegrationFlow
        {
            Nodes =
            {
                new IntegrationStep
                {
                    Id = Guid.NewGuid(),
                    NodeName = "FetchShopifyOrders",
                    StepType = StepType.HttpAction,
                    HttpMethod = "POST",
                    EndpointUrl = "https://shop.example.test/graphql.json",
                    StepConfig = "{\"apiPacket\":{\"headersJson\":\"{}\",\"queryParamsJson\":\"{}\",\"contentType\":\"application/json\",\"requestBody\":\"{\\\"query\\\":\\\"query($cursor: String) { orders(after: $cursor) { edges { node { id } } pageInfo { hasNextPage endCursor } } }\\\",\\\"variables\\\":{\\\"cursor\\\":null}}\"},\"pagination\":{\"style\":\"Cursor\",\"maxPages\":10,\"aggregatePath\":\"data.orders.edges\",\"cursorResponsePath\":\"data.orders.pageInfo.endCursor\",\"cursorRequestParameter\":\"variables.cursor\",\"hasMoreResponsePath\":\"data.orders.pageInfo.hasNextPage\"}}"
                }
            }
        };
        flowRepository.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        executionRepository.Seed(execution);
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, new StubCrossReferenceRepository(), new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(2, transport.RequestedBodies.Count);
        Assert.Contains("\"cursor\":null", transport.RequestedBodies[0]);
        Assert.Contains("\"cursor\":\"cursor-1\"", transport.RequestedBodies[1]);
        Assert.All(transport.RequestedUrls, url => Assert.DoesNotContain("cursor-1", url, StringComparison.Ordinal));
        var payload = executionRepository.StepExecutions.Single().ResponsePayload;
        Assert.Contains("\"id\":1", payload);
        Assert.Contains("\"id\":2", payload);
    }

    [Fact]
    public async Task HttpAction_RepeatedBodyContinuationFailsWithActionableMessage()
    {
        var transport = new RecordingPaginationTransport(
            new TransportResponse(200, "{\"data\":{\"orders\":{\"edges\":[{\"node\":{\"id\":1}}],\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"cursor-1\"}}}}", new Dictionary<string, string[]>(), "https://shop.example.test/graphql.json"),
            new TransportResponse(200, "{\"data\":{\"orders\":{\"edges\":[{\"node\":{\"id\":1}}],\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"cursor-1\"}}}}", new Dictionary<string, string[]>(), "https://shop.example.test/graphql.json"));
        var flowRepository = new StubIntegrationRepository();
        var executionRepository = new StubExecutionRepository();
        var flow = new IntegrationFlow
        {
            Nodes =
            {
                new IntegrationStep
                {
                    Id = Guid.NewGuid(),
                    NodeName = "FetchShopifyOrders",
                    StepType = StepType.HttpAction,
                    HttpMethod = "POST",
                    EndpointUrl = "https://shop.example.test/graphql.json",
                    StepConfig = "{\"apiPacket\":{\"headersJson\":\"{}\",\"queryParamsJson\":\"{}\",\"contentType\":\"application/json\",\"requestBody\":\"{\\\"variables\\\":{\\\"cursor\\\":null}}\"},\"pagination\":{\"style\":\"Cursor\",\"maxPages\":10,\"aggregatePath\":\"data.orders.edges\",\"cursorResponsePath\":\"data.orders.pageInfo.endCursor\",\"cursorRequestParameter\":\"variables.cursor\",\"hasMoreResponsePath\":\"data.orders.pageInfo.hasNextPage\"}}"
                }
            }
        };
        flowRepository.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        executionRepository.Seed(execution);
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, new StubCrossReferenceRepository(), new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("repeated the same continuation value", result.ErrorMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("variables.cursor", result.ErrorMessage, System.StringComparison.Ordinal);
        Assert.Contains("data.orders.pageInfo.endCursor", result.ErrorMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpAction_HasMoreResponsePathStopsCursorPagination()
    {
        var transport = new RecordingPaginationTransport(
            new TransportResponse(200, "{\"data\":{\"orders\":{\"edges\":[{\"node\":{\"id\":1}}],\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":\"cursor-1\"}}}}", new Dictionary<string, string[]>(), "https://shop.example.test/graphql.json"));
        var flowRepository = new StubIntegrationRepository();
        var executionRepository = new StubExecutionRepository();
        var flow = new IntegrationFlow
        {
            Nodes =
            {
                new IntegrationStep
                {
                    Id = Guid.NewGuid(),
                    NodeName = "FetchShopifyOrders",
                    StepType = StepType.HttpAction,
                    HttpMethod = "POST",
                    EndpointUrl = "https://shop.example.test/graphql.json",
                    StepConfig = "{\"apiPacket\":{\"headersJson\":\"{}\",\"queryParamsJson\":\"{}\",\"contentType\":\"application/json\",\"requestBody\":\"{\\\"variables\\\":{\\\"cursor\\\":null}}\"},\"pagination\":{\"style\":\"Cursor\",\"maxPages\":10,\"aggregatePath\":\"data.orders.edges\",\"cursorResponsePath\":\"data.orders.pageInfo.endCursor\",\"cursorRequestParameter\":\"variables.cursor\",\"hasMoreResponsePath\":\"data.orders.pageInfo.hasNextPage\"}}"
                }
            }
        };
        flowRepository.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        executionRepository.Seed(execution);
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, new StubCrossReferenceRepository(), new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Single(transport.RequestedBodies);
    }

    private sealed class RecordingPaginationTransport : ITransportEngine
    {
        private readonly Queue<TransportResponse> _responses;

        public RecordingPaginationTransport(params TransportResponse[] responses)
        {
            _responses = new Queue<TransportResponse>(responses);
        }

        public List<string> RequestedUrls { get; } = new();
        public List<string?> RequestedBodies { get; } = new();

        public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default, FlowTestContext? testContext = null)
        {
            RequestedUrls.Add(step.EndpointUrl);
            RequestedBodies.Add(payload);
            var response = _responses.Dequeue();
            var now = DateTime.UtcNow;
            var attempt = new TransportAttempt(response.StatusCode, response.Response, response.Headers, step.EndpointUrl, step.HttpMethod, new Dictionary<string, string[]>(), payload ?? string.Empty, now, now);
            return Task.FromResult(response with { RequestUrl = step.EndpointUrl, HttpMethod = step.HttpMethod, RequestBody = payload ?? string.Empty, StartedAt = now, CompletedAt = now, Attempts = new[] { attempt } });
        }
    }
}

