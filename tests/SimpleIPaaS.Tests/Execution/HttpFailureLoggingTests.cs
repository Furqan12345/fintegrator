using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class HttpFailureLoggingTests
{
    [Fact]
    public async Task TransportFailure_RecordsPacketRequestAndActionableError()
    {
        var failedAt = DateTime.UtcNow;
        const string failure = "The SSL connection could not be established";
        var requestHeaders = new Dictionary<string, string[]> { ["X-Test"] = new[] { "value" } };
        var transport = new FixedTransport(new TransportResponse(0, failure, new Dictionary<string, string[]>(), "https://api.example.test/orders")
        {
            Error = failure,
            HttpMethod = "POST",
            RequestHeaders = requestHeaders,
            RequestBody = "{\"query\":\"orders\"}",
            StartedAt = failedAt.AddSeconds(-1),
            CompletedAt = failedAt,
            Attempts = new[]
            {
                new TransportAttempt(0, string.Empty, new Dictionary<string, string[]>(), "https://api.example.test/orders", "POST",
                    requestHeaders, "{\"query\":\"orders\"}", failedAt.AddSeconds(-1), failedAt, failure)
            }
        });
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
                    HttpMethod = "POST",
                    EndpointUrl = "https://api.example.test/orders",
                    StepConfig = "{\"apiPacket\":{\"headersJson\":\"{}\",\"queryParamsJson\":\"{}\",\"contentType\":\"application/json\",\"requestBody\":\"{\\\"query\\\":\\\"orders\\\"}\"}}"
                }
            }
        };
        flowRepository.Seed(flow);
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        executionRepository.Seed(execution);
        var executor = new FlowExecutor(flowRepository, transport, new UnreachableCodeExecutionService(), executionRepository, new StubCrossReferenceRepository(), new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("before a response was received", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(failure, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var step = executionRepository.StepExecutions.Single();
        Assert.Equal("{\"query\":\"orders\"}", step.RequestPayload);
        Assert.Equal(ExecutionStatus.Failed, step.Status);

        var packet = executionRepository.StepPacketLogs.Single(log => log.Kind == "HTTP");
        Assert.Equal("{\"query\":\"orders\"}", packet.RequestBody);
        Assert.Equal(failure, packet.Error);
        Assert.Null(packet.StatusCode);
        Assert.Contains("X-Test", packet.RequestHeadersJson, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedTransport : ITransportEngine
    {
        private readonly TransportResponse _response;

        public FixedTransport(TransportResponse response) => _response = response;

        public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default, FlowTestContext? testContext = null) =>
            Task.FromResult(_response);
    }
}

