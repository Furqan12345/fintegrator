using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public class DeadLetterRecoveryTests
{
    private static readonly JsonSerializerOptions AttemptOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();
    private readonly StubTransportEngine _transport = new();

    private DeadLetterService CreateService()
    {
        var executor = new FlowExecutor(
            _flows,
            _transport,
            new UnreachableCodeExecutionService(),
            _executions,
            new StubCrossReferenceRepository(),
            NullLogger<FlowExecutor>.Instance);

        return new DeadLetterService(_executions, _flows, executor, NullLogger<DeadLetterService>.Instance);
    }

    private static IntegrationStep HttpNode(string name) => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.HttpAction,
        HttpMethod = "GET",
        EndpointUrl = "https://example.test/orders"
    };

    private (IntegrationFlow Flow, FlowExecution Execution) SeedFailedRun(params IntegrationStep[] nodes)
    {
        var flow = new IntegrationFlow { Name = "Order Sync" };
        foreach (var node in nodes)
        {
            flow.Nodes.Add(node);
        }

        _flows.Seed(flow);

        var execution = new FlowExecution
        {
            FlowId = flow.Id,
            FlowName = flow.Name,
            Status = ExecutionStatus.Failed,
            TriggerSource = "Test",
            TotalRecords = nodes.Length,
            FailedRecords = nodes.Length,
            CompletedAt = DateTime.UtcNow
        };
        _executions.Seed(execution);

        return (flow, execution);
    }

    private DeadLetterEntry SeedDeadLetter(FlowExecution execution, IntegrationStep node, string error)
    {
        var step = new StepExecution
        {
            FlowExecutionId = execution.Id,
            StepId = node.Id,
            NodeName = node.NodeName,
            Status = ExecutionStatus.Failed,
            ErrorMessage = error,
            CompletedAt = DateTime.UtcNow
        };
        _executions.StepExecutions.Add(step);

        var entry = new DeadLetterEntry
        {
            FlowExecutionId = execution.Id,
            StepId = node.Id,
            FlowName = "Order Sync",
            NodeName = node.NodeName,
            ErrorMessage = error,
            Status = "Pending"
        };
        _executions.DeadLetters.Add(entry);

        return entry;
    }

    private static List<AttemptRecord> Attempts(string json) =>
        JsonSerializer.Deserialize<List<AttemptRecord>>(json, AttemptOptions) ?? new List<AttemptRecord>();

    [Fact]
    public async Task ReplayEntryAsync_FlipsTheStepToRecovered_AndKeepsTheOriginalErrorMessage()
    {
        var node = HttpNode("Push");
        var (_, execution) = SeedFailedRun(node);
        var entry = SeedDeadLetter(execution, node, "Connection refused");

        _transport.Enqueue(200, "{}");

        var result = await CreateService().ReplayEntryAsync(entry.Id, force: true);

        Assert.Equal(DeadLetterReplayOutcome.Replayed, result.Outcome);
        Assert.Equal("Resolved", entry.Status);
        Assert.NotNull(entry.ResolvedAt);
        Assert.Equal("Connection refused", entry.ErrorMessage);

        var step = Assert.Single(_executions.StepExecutions);
        Assert.Equal(ExecutionStatus.Recovered, step.Status);
        Assert.Equal("Connection refused", step.ErrorMessage);
        Assert.NotNull(step.RecoveredAt);
        Assert.Equal(entry.Id, step.RecoveredByDeadLetterId);
    }

    [Fact]
    public async Task ReplayEntryAsync_AppendsEverySuccessAndFailureToTheAttemptHistory()
    {
        var node = HttpNode("Push");
        var (_, execution) = SeedFailedRun(node);
        var entry = SeedDeadLetter(execution, node, "Connection refused");

        var service = CreateService();

        _transport.Enqueue(500, "upstream exploded");
        await service.ReplayEntryAsync(entry.Id, force: true);

        _transport.Enqueue(200, "{}");
        await service.ReplayEntryAsync(entry.Id, force: true);

        var attempts = Attempts(entry.AttemptHistoryJson);

        Assert.Equal(2, attempts.Count);
        Assert.Equal(1, attempts[0].Attempt);
        Assert.Equal(500, attempts[0].StatusCode);
        Assert.Contains("upstream exploded", attempts[0].Error);
        Assert.Equal(2, attempts[1].Attempt);
        Assert.Equal(200, attempts[1].StatusCode);
        Assert.Empty(attempts[1].Error);
    }

    [Fact]
    public async Task ReplayEntryAsync_LeavesTheExecutionFailed_WhileAnotherDeadLetterIsStillPending()
    {
        var first = HttpNode("Push A");
        var second = HttpNode("Push B");
        var (_, execution) = SeedFailedRun(first, second);
        var entry = SeedDeadLetter(execution, first, "First failed");
        SeedDeadLetter(execution, second, "Second failed");

        _transport.Enqueue(200, "{}");
        await CreateService().ReplayEntryAsync(entry.Id, force: true);

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Null(execution.RecoveredAt);
        Assert.Equal(2, execution.FailedRecords);
        Assert.Equal(0, execution.SuccessRecords);
    }

    [Fact]
    public async Task ReplayEntryAsync_FlipsTheExecutionToRecovered_OnceEveryDeadLetterIsResolved()
    {
        var first = HttpNode("Push A");
        var second = HttpNode("Push B");
        var (_, execution) = SeedFailedRun(first, second);
        var firstEntry = SeedDeadLetter(execution, first, "First failed");
        var secondEntry = SeedDeadLetter(execution, second, "Second failed");

        var service = CreateService();

        _transport.Enqueue(200, "{}");
        await service.ReplayEntryAsync(firstEntry.Id, force: true);

        _transport.Enqueue(200, "{}");
        await service.ReplayEntryAsync(secondEntry.Id, force: true);

        Assert.Equal(ExecutionStatus.Recovered, execution.Status);
        Assert.NotNull(execution.RecoveredAt);
        Assert.Equal(0, execution.FailedRecords);
        Assert.Equal(2, execution.SuccessRecords);
        Assert.All(_executions.StepExecutions, step => Assert.Equal(ExecutionStatus.Recovered, step.Status));
    }

    private sealed class AttemptRecord
    {
        public int Attempt { get; set; }
        public DateTime At { get; set; }
        public int? StatusCode { get; set; }
        public string Error { get; set; } = string.Empty;
    }
}
