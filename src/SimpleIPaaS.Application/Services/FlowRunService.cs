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
    private readonly IIntegrationRepository _integrationRepository;
    private readonly IIntegrationCatalogRepository _integrationCatalogRepository;
    private readonly ExecutionCancellationRegistry _cancellationRegistry;

    public FlowRunService(
        IExecutionQueue queue,
        IExecutionRepository executionRepository,
        IIntegrationRepository integrationRepository,
        IIntegrationCatalogRepository integrationCatalogRepository,
        ExecutionCancellationRegistry cancellationRegistry)
    {
        _queue = queue;
        _executionRepository = executionRepository;
        _integrationRepository = integrationRepository;
        _integrationCatalogRepository = integrationCatalogRepository;
        _cancellationRegistry = cancellationRegistry;
    }

    public async Task<Guid> EnqueueAsync(Guid flowId, Guid tenantId, string triggerSource, string? triggerPayload, CancellationToken cancellationToken = default)
    {
        var flow = await _integrationRepository.GetByIdAsync(flowId);
        var integrationName = string.Empty;

        if (flow?.IntegrationId is Guid integrationId && integrationId != Guid.Empty)
        {
            var integration = await _integrationCatalogRepository.GetByIdAsync(integrationId);
            integrationName = integration?.Name ?? string.Empty;
        }

        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flowId,
            TenantId = tenantId,
            FlowName = flow?.Name ?? string.Empty,
            IntegrationId = flow?.IntegrationId,
            IntegrationName = integrationName,
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
