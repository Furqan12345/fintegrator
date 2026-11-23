using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class DeadLetterResyncFixTests
{
    private static readonly JsonSerializerOptions AttemptOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private DeadLetterService CreateService(ITransportEngine transport, int? maxAutoRetryAttempts = null)
    {
        var executor = new FlowExecutor(
            _flows,
            transport,
            new UnreachableCodeExecutionService(),
            _executions,
            new StubCrossReferenceRepository(),
            new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

        IConfiguration? configuration = maxAutoRetryAttempts.HasValue
            ? new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DeadLetter:MaxAutoRetryAttempts"] = maxAutoRetryAttempts.Value.ToString()
                })
                .Build()
            : null;

        return new DeadLetterService(_executions, _flows, executor, NullLogger<DeadLetterService>.Instance, configuration);
    }

    private (IntegrationFlow Flow, FlowExecution Execution, DeadLetterEntry Entry) Seed(string rootError, string httpMethod = "GET", bool allowPostReplay = false)
    {
        var node = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "FetchOrders",
            StepType = StepType.HttpAction,
            HttpMethod = httpMethod,
            EndpointUrl = "https://example.test/orders"
        };
        var flow = new IntegrationFlow { Name = "Order Sync", AllowPostReplay = allowPostReplay };
        flow.Nodes.Add(node);
        _flows.Seed(flow);

        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flow.Id,
            FlowName = flow.Name,
            Status = ExecutionStatus.Failed,
            TriggerSource = "Test",
            FailedRecords = 1
        };
        _executions.Seed(execution);

        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowExecutionId = execution.Id,
            StepId = node.Id,
            FlowName = flow.Name,
            NodeName = node.NodeName,
            ErrorMessage = rootError,
            Status = "Pending",
            Payload = "{}",
            FlowStateJson = "{}",
            PersistedStateJson = "{}",
            AttemptHistoryJson = JsonSerializer.Serialize(new[]
            {
                new { Attempt = 0, At = DateTime.UtcNow, StatusCode = (int?)null, Error = rootError, NodeName = node.NodeName, Kind = "Failure" }
            })
        };
        _executions.DeadLetters.Add(entry);
        return (flow, execution, entry);
    }

    [Fact]
    public async Task FailedReplay_PreservesRootErrorAndReturnsToPending()
    {
        var transport = new FixedTransport(500, "{\"error\":\"upstream broke\"}");
        var (_, _, entry) = Seed("TLS handshake failed");
        var service = CreateService(transport);

        var result = await service.ReplayEntryAsync(entry.Id, force: true);

        Assert.Equal(DeadLetterReplayOutcome.Replayed, result.Outcome);
        Assert.Equal("TLS handshake failed", entry.ErrorMessage);
        Assert.Equal("Pending", entry.Status);
        Assert.Equal(1, entry.RetryCount);

        var history = JsonSerializer.Deserialize<List<DeadLetterAttempt>>(entry.AttemptHistoryJson, AttemptOptions)!.ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("Failure", history[0].Kind);
        Assert.Contains("upstream broke", history[1].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkippedReplay_ResetsRetryingToPendingSoItCannotWedge()
    {
        var transport = new FixedTransport(200, "{}");
        var (_, _, entry) = Seed("boom");
        entry.Status = "Retrying";
        entry.RetryCount = 5;
        var service = CreateService(transport, maxAutoRetryAttempts: 1);

        var result = await service.ReplayEntryAsync(entry.Id, force: false);

        Assert.Equal(DeadLetterReplayOutcome.Skipped, result.Outcome);
        Assert.Equal("Pending", entry.Status);
    }

    [Fact]
    public async Task BlockedBySideEffectingNode_RecordsReasonOnceAndStaysPending()
    {
        var transport = new FixedTransport(200, "{}");
        var (_, _, entry) = Seed("boom", httpMethod: "POST", allowPostReplay: false);
        var service = CreateService(transport, maxAutoRetryAttempts: 10);

        var first = await service.ReplayEntryAsync(entry.Id, force: true);
        Assert.Equal(DeadLetterReplayOutcome.PostReplayNotAllowed, first.Outcome);
        Assert.Equal("Pending", entry.Status);

        var second = await service.ReplayEntryAsync(entry.Id, force: true);
        Assert.Equal(DeadLetterReplayOutcome.PostReplayNotAllowed, second.Outcome);

        var history = JsonSerializer.Deserialize<List<DeadLetterAttempt>>(entry.AttemptHistoryJson, AttemptOptions)!.ToList();
        Assert.Equal(2, history.Count); // original failure + one blocked note, not one per scan
        Assert.Equal("Blocked", history[1].Kind);
    }

    [Fact]
    public async Task SideEffectingNode_ReplaysWhenPostReplayAllowed()
    {
        var transport = new FixedTransport(200, "{\"ok\":true}");
        var (flow, execution, entry) = Seed("boom", httpMethod: "POST", allowPostReplay: true);
        var failedStep = new StepExecution
        {
            Id = Guid.NewGuid(),
            FlowExecutionId = execution.Id,
            StepId = entry.StepId,
            NodeName = "FetchOrders",
            Status = ExecutionStatus.Failed,
            ErrorMessage = "boom",
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        _executions.StepExecutions.Add(failedStep);

        var service = CreateService(transport, maxAutoRetryAttempts: 10);
        var result = await service.ReplayEntryAsync(entry.Id, force: true);

        Assert.Equal(DeadLetterReplayOutcome.Replayed, result.Outcome);
        Assert.Equal("Resolved", entry.Status);
        Assert.NotNull(entry.ResolvedAt);
    }

    [Fact]
    public async Task AtomicClaims_AreWonExactlyOnce()
    {
        using var connection = new SqliteConnection("Data Source=DeadLetterClaims;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;
        var entryId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = "Order Sync" });
            context.IntegrationSteps.Add(new IntegrationStep
            {
                Id = stepId,
                FlowId = flowId,
                NodeName = "FetchOrders",
                StepType = StepType.HttpAction
            });
            context.FlowExecutions.Add(new FlowExecution
            {
                Id = runId,
                FlowId = flowId,
                FlowName = "Order Sync",
                Status = ExecutionStatus.Failed
            });
            context.DeadLetterEntries.Add(new DeadLetterEntry
            {
                Id = entryId,
                FlowExecutionId = runId,
                StepId = stepId,
                Status = "Pending",
                ReplayRequestedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // The worker claims by id alone; no request scope is involved.
        using (var context = new IPaaSContext(options))
        {
            var repository = new ExecutionRepository(context);

            Assert.True(await repository.TryClaimDeadLetterForManualReplayAsync(entryId));
            Assert.False(await repository.TryClaimDeadLetterForManualReplayAsync(entryId));

            Assert.True(await repository.TryClaimDeadLetterForAutoRetryAsync(entryId));
            Assert.False(await repository.TryClaimDeadLetterForAutoRetryAsync(entryId));

            var reset = await repository.ResetStaleRetryingDeadLettersAsync(TimeSpan.FromMinutes(30));
            Assert.Equal(0, reset);
            Assert.True(await repository.ResetStaleRetryingDeadLettersAsync(TimeSpan.FromTicks(1)) >= 1);
        }

        using (var context = new IPaaSContext(options))
        {
            var repository = new ExecutionRepository(context);
            var page = await repository.GetDeadLetterPageAsync(new DeadLetterPageFilter());
            var row = Assert.Single(page);
            Assert.Equal(entryId, row.Entry.Id);
            Assert.Equal("Pending", row.Entry.Status);
        }
    }

    [Fact]
    public async Task ManualReplayRequest_PersistsEditedFlowStateWithTheClaim()
    {
        using var connection = new SqliteConnection("Data Source=DeadLetterStateEdit;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;
        var flowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = "Order Sync" });
            context.IntegrationSteps.Add(new IntegrationStep
            {
                Id = stepId,
                FlowId = flowId,
                NodeName = "FetchOrders",
                StepType = StepType.HttpAction
            });
            context.FlowExecutions.Add(new FlowExecution
            {
                Id = runId,
                FlowId = flowId,
                FlowName = "Order Sync",
                Status = ExecutionStatus.Failed
            });
            context.DeadLetterEntries.Add(new DeadLetterEntry
            {
                Id = entryId,
                FlowExecutionId = runId,
                StepId = stepId,
                Status = "Pending",
                FlowStateJson = "{}"
            });
            await context.SaveChangesAsync();
        }

        var startStepId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            var repository = new ExecutionRepository(context);
            Assert.True(await repository.TryRequestManualReplayAsync(
                entryId, startStepId, DateTime.UtcNow, "{\"edited\":true}"));

            // A losing concurrent request must not overwrite the state of the queued replay.
            Assert.False(await repository.TryRequestManualReplayAsync(
                entryId, Guid.NewGuid(), DateTime.UtcNow, "{\"edited\":false}"));
        }

        using (var context = new IPaaSContext(options))
        {
            var entry = await context.DeadLetterEntries.SingleAsync(item => item.Id == entryId);
            Assert.Equal("Retrying", entry.Status);
            Assert.Equal(startStepId, entry.ReplayFromStepId);
            Assert.NotNull(entry.ReplayRequestedAt);
            Assert.Equal("{\"edited\":true}", entry.FlowStateJson);
        }
    }

    private sealed class FixedTransport : ITransportEngine
    {
        private readonly int _statusCode;
        private readonly string _body;

        public FixedTransport(int statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default, FlowTestContext? testContext = null)
        {
            var now = DateTime.UtcNow;
            var attempt = new TransportAttempt(_statusCode, _body, new Dictionary<string, string[]>(), step.EndpointUrl, step.HttpMethod, new Dictionary<string, string[]>(), payload ?? string.Empty, now, now);
            return Task.FromResult(new TransportResponse(_statusCode, _body, new Dictionary<string, string[]>(), step.EndpointUrl)
            {
                Attempts = new[] { attempt }
            });
        }
    }
}


