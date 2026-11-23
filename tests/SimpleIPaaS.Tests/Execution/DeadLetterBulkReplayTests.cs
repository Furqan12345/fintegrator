using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class DeadLetterBulkReplayTests
{
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private static IntegrationStep Node(string name = "Push", string method = "GET") => new()
    {
        Id = Guid.NewGuid(),
        NodeName = name,
        StepType = StepType.HttpAction,
        HttpMethod = method,
        EndpointUrl = "https://example.test/orders"
    };

    private (IntegrationFlow Flow, FlowExecution Execution, DeadLetterEntry Entry) Seed(
        string flowName = "Order Sync",
        string status = "Pending",
        string method = "GET",
        bool allowPostReplay = false)
    {
        var node = Node(method: method);
        var flow = new IntegrationFlow
        {
            Id = Guid.NewGuid(),
            Name = flowName,
            AllowPostReplay = allowPostReplay
        };
        flow.Nodes.Add(node);
        _flows.Seed(flow);

        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flow.Id,
            FlowName = flow.Name,
            Status = ExecutionStatus.Failed
        };
        _executions.Seed(execution);

        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowExecutionId = execution.Id,
            StepId = node.Id,
            FlowName = flow.Name,
            NodeName = node.NodeName,
            ErrorMessage = "boom",
            Status = status,
            Payload = "{}",
            FlowStateJson = "{}",
            PersistedStateJson = "{}"
        };
        _executions.DeadLetters.Add(entry);

        return (flow, execution, entry);
    }

    private DeadLetterEntry SeedEmail(bool allowPostReplay)
    {
        var upstream = Node("Map");
        var email = new IntegrationStep
        {
            Id = Guid.NewGuid(),
            NodeName = "Notify",
            StepType = StepType.Email,
            StepConfig = new EmailStepConfig
            {
                To = "ops@example.com",
                Subject = "Order synced",
                Body = "Body"
            }.Write()
        };
        var flow = new IntegrationFlow { Name = "Email flow", AllowPostReplay = allowPostReplay };
        flow.Nodes.Add(upstream);
        flow.Nodes.Add(email);
        flow.Edges.Add(new IntegrationEdge
        {
            SourceNodeId = upstream.Id,
            TargetNodeId = email.Id,
            SourcePortId = "right",
            TargetPortId = "left"
        });
        _flows.Seed(flow);

        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flow.Id,
            FlowName = flow.Name,
            Status = ExecutionStatus.Failed
        };
        _executions.Seed(execution);
        _executions.StepExecutions.Add(new StepExecution
        {
            FlowExecutionId = execution.Id,
            StepId = upstream.Id,
            NodeName = upstream.NodeName,
            Status = ExecutionStatus.Success,
            CompletedAt = DateTime.UtcNow
        });

        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowExecutionId = execution.Id,
            StepId = email.Id,
            FlowName = flow.Name,
            NodeName = email.NodeName,
            ErrorMessage = "invalid",
            Status = "Pending",
            Payload = "{}",
            FlowStateJson = "{}",
            PersistedStateJson = "{}"
        };
        _executions.DeadLetters.Add(entry);
        return entry;
    }

    private static DeadLetterController CreateController(
        StubExecutionRepository executions,
        StubIntegrationRepository flows)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();

        return new DeadLetterController(executions, flows, NullLogger<DeadLetterController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() }
            }
        };
    }

    private static BulkRetryDeadLettersResultDto OkResult(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsAssignableFrom<BulkRetryDeadLettersResultDto>(ok.Value);
    }

    [Fact]
    public async Task EligibleEntriesAreQueuedWithTheirDefaultStartNode()
    {
        var (_, _, entry) = Seed();
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { entry.Id }
        }));

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Queued);
        Assert.Contains(entry.Id, result.QueuedIds);
        Assert.Equal("Retrying", entry.Status);
        Assert.NotNull(entry.ReplayRequestedAt);
        Assert.Equal(entry.StepId, entry.ReplayFromStepId);
    }

    [Fact]
    public async Task ResolvedAndDiscardedEntriesAreSkipped()
    {
        var resolved = Seed(status: "Resolved").Entry;
        var discarded = Seed(status: "Discarded").Entry;
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { resolved.Id, discarded.Id }
        }));

        Assert.Equal(0, result.Queued);
        Assert.Equal(2, result.Skipped);
        Assert.All(result.Skips, skip => Assert.Contains("Only Pending or Retrying", skip.Reason));
        Assert.Null(resolved.ReplayRequestedAt);
        Assert.Null(discarded.ReplayRequestedAt);
    }

    [Fact]
    public async Task UnsafePostPathIsSkippedWithAReason()
    {
        var entry = Seed(method: "POST", allowPostReplay: false).Entry;
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { entry.Id }
        }));

        Assert.Equal(0, result.Queued);
        Assert.Equal(1, result.Skipped);
        var skip = Assert.Single(result.Skips);
        Assert.Contains("side-effecting", skip.Reason);
        Assert.Contains("post replay", skip.Reason);
        Assert.Null(entry.ReplayRequestedAt);
    }

    [Fact]
    public async Task SecondIdenticalCallReturnsAlreadyQueued()
    {
        var entry = Seed().Entry;
        var controller = CreateController(_executions, _flows);
        var request = new BulkRetryDeadLettersRequestDto { Ids = new List<Guid> { entry.Id } };

        var first = OkResult(await controller.BulkRetry(request));
        var second = OkResult(await controller.BulkRetry(request));

        Assert.Equal(1, first.Queued);
        Assert.Equal(0, second.Queued);
        Assert.Equal(1, second.AlreadyQueued);
        Assert.Equal("Retrying", entry.Status);
    }


    [Fact]
    public async Task DryRunChangesNothing()
    {
        var entry = Seed().Entry;
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { entry.Id },
            DryRun = true
        }));

        Assert.Equal(1, result.Queued);
        Assert.Empty(result.QueuedIds);
        Assert.Equal("Pending", entry.Status);
        Assert.Null(entry.ReplayRequestedAt);
        Assert.Null(entry.ReplayFromStepId);
    }

    [Fact]
    public async Task FilterModeOnlyConsidersPendingEntries()
    {
        var pending = Seed(status: "Pending");
        _executions.DeadLetters.Add(new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            FlowExecutionId = pending.Execution.Id,
            StepId = pending.Entry.StepId,
            FlowName = pending.Flow.Name,
            NodeName = pending.Entry.NodeName,
            ErrorMessage = "already resolved",
            Status = "Resolved",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            FlowId = pending.Flow.Id
        }));

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Queued);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task EmailNode_IsSkippedWithTheEmailReason()
    {
        var entry = SeedEmail(allowPostReplay: false);
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { entry.Id }
        }));

        Assert.Equal(0, result.Queued);
        Assert.Equal(1, result.Skipped);
        Assert.Contains("Replay would resend an email", Assert.Single(result.Skips).Reason);
    }

    [Fact]
    public async Task EmailNode_IsQueuedWhenPostReplayIsAllowed()
    {
        var entry = SeedEmail(allowPostReplay: true);
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { entry.Id }
        }));

        Assert.Equal(1, result.Queued);
        Assert.Equal("Retrying", entry.Status);
    }

    [Fact]
    public async Task ExecutionScopedFilterOnlyQueuesThatExecutionsEntries()
    {
        var first = Seed();
        var second = Seed();
        var controller = CreateController(_executions, _flows);

        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            ExecutionId = first.Execution.Id
        }));

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.TotalMatched);
        Assert.Equal(1, result.Queued);
        Assert.Equal("Retrying", first.Entry.Status);
        Assert.Equal("Pending", second.Entry.Status);
    }

    [Fact]
    public async Task FilterModePagesThroughSixHundredMatchingEntries()
    {
        var seed = Seed();
        for (var index = 0; index < 599; index++)
        {
            _executions.DeadLetters.Add(new DeadLetterEntry
            {
                Id = Guid.NewGuid(),
                FlowExecutionId = seed.Execution.Id,
                StepId = seed.Entry.StepId,
                FlowName = seed.Flow.Name,
                NodeName = seed.Entry.NodeName,
                ErrorMessage = "boom",
                Status = "Pending",
                Payload = "{}",
                FlowStateJson = "{}",
                PersistedStateJson = "{}",
                CreatedAt = DateTime.UtcNow.AddSeconds(-index)
            });
        }

        var controller = CreateController(_executions, _flows);
        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            FlowId = seed.Flow.Id
        }));

        Assert.Equal(600, result.Matched);
        Assert.Equal(600, result.TotalMatched);
        Assert.Equal(600, result.Queued);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task FilterModeCapsAtFiveThousandAndReportsTruncation()
    {
        var seed = Seed();
        for (var index = 0; index < 5000; index++)
        {
            _executions.DeadLetters.Add(new DeadLetterEntry
            {
                Id = Guid.NewGuid(),
                FlowExecutionId = seed.Execution.Id,
                StepId = seed.Entry.StepId,
                FlowName = seed.Flow.Name,
                NodeName = seed.Entry.NodeName,
                ErrorMessage = "boom",
                Status = "Pending",
                Payload = "{}",
                FlowStateJson = "{}",
                PersistedStateJson = "{}",
                CreatedAt = DateTime.UtcNow.AddSeconds(-index)
            });
        }

        var controller = CreateController(_executions, _flows);
        var result = OkResult(await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            FlowId = seed.Flow.Id
        }));

        Assert.Equal(5000, result.TotalMatched);
        Assert.Equal(5000, result.Queued);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task TestRunEntriesAreHiddenAndReplayIsRefused()
    {
        var (_, execution, entry) = Seed();
        execution.IsTest = true;
        var controller = CreateController(_executions, _flows);

        var listed = Assert.IsType<OkObjectResult>(await controller.GetAll());
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<DeadLetterDto>>(listed.Value));
        Assert.IsType<BadRequestObjectResult>(await controller.Retry(entry.Id, null));

        var bulk = await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { entry.Id }
        });
        Assert.IsType<BadRequestObjectResult>(bulk);
        Assert.Equal("Pending", entry.Status);
        Assert.Null(entry.ReplayRequestedAt);
    }

    [Fact]
    public async Task NoIdsAndNoFlowIdReturnsBadRequest()
    {
        var controller = CreateController(_executions, _flows);

        var result = await controller.BulkRetry(new BulkRetryDeadLettersRequestDto());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task StartStepIdWithMixedFlowsReturnsBadRequest()
    {
        var first = Seed(flowName: "First flow").Entry;
        var second = Seed(flowName: "Second flow").Entry;
        var controller = CreateController(_executions, _flows);

        var result = await controller.BulkRetry(new BulkRetryDeadLettersRequestDto
        {
            Ids = new List<Guid> { first.Id, second.Id },
            StartStepId = first.StepId
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Pending", first.Status);
        Assert.Equal("Pending", second.Status);
    }
}

