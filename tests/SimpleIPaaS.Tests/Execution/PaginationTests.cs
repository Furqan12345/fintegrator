using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
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
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, crossReferenceRepository, NullLogger<FlowExecutor>.Instance);

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
    public async Task HttpAction_FailsClearlyWhenPaginationLimitIsReached()
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
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, new StubCrossReferenceRepository(), NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("incomplete after 1 pages", result.ErrorMessage);
    }

    private sealed class RecordingPaginationTransport : ITransportEngine
    {
        private readonly Queue<TransportResponse> _responses;

        public RecordingPaginationTransport(params TransportResponse[] responses)
        {
            _responses = new Queue<TransportResponse>(responses);
        }

        public List<string> RequestedUrls { get; } = new();

        public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(step.EndpointUrl);
            var response = _responses.Dequeue();
            var now = DateTime.UtcNow;
            var attempt = new TransportAttempt(response.StatusCode, response.Response, response.Headers, step.EndpointUrl, step.HttpMethod, new Dictionary<string, string[]>(), payload ?? string.Empty, now, now);
            return Task.FromResult(response with { RequestUrl = step.EndpointUrl, HttpMethod = step.HttpMethod, RequestBody = payload ?? string.Empty, StartedAt = now, CompletedAt = now, Attempts = new[] { attempt } });
        }
    }
}
