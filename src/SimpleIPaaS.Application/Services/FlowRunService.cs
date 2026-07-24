using System;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public class FlowRunService
{
    private readonly IExecutionQueue _queue;
    private readonly IExecutionRepository _executionRepository;
    private readonly ExecutionCancellationRegistry _cancellationRegistry;

    public FlowRunService(
        IExecutionQueue queue,
        IExecutionRepository executionRepository,
        ExecutionCancellationRegistry cancellationRegistry)
    {
        _queue = queue;
        _executionRepository = executionRepository;
        _cancellationRegistry = cancellationRegistry;
    }

    public async Task<Guid> EnqueueAsync(Guid flowId, Guid tenantId, string triggerSource, string? triggerPayload, CancellationToken cancellationToken = default)
    {
        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flowId,
            TenantId = tenantId,
            Status = ExecutionStatus.Queued,
            StartedAt = DateTime.UtcNow,
            TriggerSource = triggerSource
        };

        await _executionRepository.AddFlowExecutionAsync(execution);
        _cancellationRegistry.GetOrCreate(execution.Id);

        await _queue.EnqueueAsync(new ExecutionRequest
        {
            ExecutionId = execution.Id,
            FlowId = flowId,
            TenantId = tenantId,
            TriggerSource = triggerSource,
            TriggerPayload = triggerPayload
        }, cancellationToken);

        return execution.Id;
    }
}
