using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class TelemetryFailureTests
{
    [Fact]
    public async Task PacketLogFailure_DoesNotMaskHttpFailureOrSuppressDeadLetter()
    {
        var flow = FlowWith(HttpNode("Push"));
        var execution = SeedExecution(flow);
        var repository = new StubExecutionRepository();
        repository.Seed(execution);
        repository.StepPacketLogException = new InvalidOperationException("packet log store unavailable");
        var executor = CreateExecutor(flow, repository, Transport(500, "upstream failed"));

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("status code 500", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var deadLetter = Assert.Single(repository.DeadLetters);
        Assert.Equal("Pending", deadLetter.Status);
        Assert.Contains("status code 500", deadLetter.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repository.StepPacketLogs);
    }

    [Fact]
    public async Task PacketLogFailure_DoesNotFailSuccessfulNonHttpStep()
    {
        var flow = FlowWith(new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Map",
            StepType = StepType.Mapping
        });
        var execution = SeedExecution(flow);
        var repository = new StubExecutionRepository();
        repository.Seed(execution);
        repository.StepPacketLogException = new InvalidOperationException("packet log store unavailable");
        var executor = CreateExecutor(flow, repository, new UnreachableTransportEngine());

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Empty(repository.DeadLetters);
        Assert.Empty(repository.StepPacketLogs);
    }

    [Fact]
    public async Task DeadLetterPersistenceFailure_DoesNotReplaceTheOriginalFailure()
    {
        var flow = FlowWith(HttpNode("Push"));
        var execution = SeedExecution(flow);
        var repository = new StubExecutionRepository();
        repository.Seed(execution);
        repository.DeadLetterException = new InvalidOperationException("dead letter store unavailable");
        var executor = CreateExecutor(flow, repository, Transport(500, "upstream failed"));

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("status code 500", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repository.DeadLetters);
    }

    private static FlowExecutor CreateExecutor(
        IntegrationFlow flow,
        StubExecutionRepository executionRepository,
        ITransportEngine transport)
    {
        var flowRepository = new StubIntegrationRepository();
        flowRepository.Seed(flow);
        return new FlowExecutor(
            flowRepository,
            transport,
            new UnreachableCodeExecutionService(),
            executionRepository,
            new StubCrossReferenceRepository(),
            new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);
    }

    private static FixedTransport Transport(int statusCode, string response)
    {
        const string url = "https://api.example.test/orders";
        var completedAt = DateTime.UtcNow;
        var startedAt = completedAt.AddMilliseconds(-10);
        var attempt = new TransportAttempt(
            0,
            string.Empty,
            new Dictionary<string, string[]>(),
            url,
            "GET",
            new Dictionary<string, string[]>(),
            string.Empty,
            startedAt,
            completedAt,
            statusCode == 0 ? response : string.Empty);

        return new FixedTransport(new TransportResponse(statusCode, response, new Dictionary<string, string[]>(), url)
        {
            Attempts = new[] { attempt },
            StartedAt = startedAt,
            CompletedAt = completedAt
        });
    }

    private static FlowExecution SeedExecution(IntegrationFlow flow) => new()
    {
        FlowId = flow.Id,
        FlowName = flow.Name,
        TriggerSource = "Test"
    };

    private static IntegrationFlow FlowWith(params IntegrationStep[] nodes)
    {
        var flow = new IntegrationFlow { Name = "Telemetry test" };
        foreach (var node in nodes)
        {
            flow.Nodes.Add(node);
        }

        return flow;
    }

    private static IntegrationStep HttpNode(string name) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.HttpAction,
        HttpMethod = "GET",
        EndpointUrl = "https://api.example.test/orders"
    };

    private sealed class FixedTransport : ITransportEngine
    {
        private readonly TransportResponse _response;

        public FixedTransport(TransportResponse response) => _response = response;

        public Task<TransportResponse> DispatchAsync(
            IntegrationStep step,
            string? payload,
            Guid? connectionId = null,
            CancellationToken cancellationToken = default,
            FlowTestContext? testContext = null) =>
            Task.FromResult(_response);
    }
}

