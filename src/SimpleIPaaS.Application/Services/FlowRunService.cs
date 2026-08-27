using System;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

// Persists flow runs as Queued rows in the database. This is the entire
// "hand-off" from the API/UI tier to the standalone execution Engine:
// the engine claims Queued FlowExecutions directly from the shared
// database; there is deliberately no HTTP call or in-memory channel.
public class FlowRunService
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IIntegrationRepository _integrationRepository;
    private readonly IIntegrationCatalogRepository _integrationCatalogRepository;

    public FlowRunService(
        IExecutionRepository executionRepository,
        IIntegrationRepository integrationRepository,
        IIntegrationCatalogRepository integrationCatalogRepository)
    {
        _executionRepository = executionRepository;
        _integrationRepository = integrationRepository;
        _integrationCatalogRepository = integrationCatalogRepository;
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

        var enqueuedAt = DateTime.UtcNow;

        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flowId,
            TenantId = tenantId,
            FlowName = flow?.Name ?? string.Empty,
            IntegrationId = flow?.IntegrationId,
            IntegrationName = integrationName,
            Status = ExecutionStatus.Queued,
            // StartedAt doubles as the FIFO key until the Engine rewrites it on claim.
            StartedAt = enqueuedAt,
            TriggerSource = triggerSource,
            // Persisted so the payload survives the process boundary to the Engine.
            TriggerPayloadJson = triggerPayload
        };

        await _executionRepository.AddFlowExecutionAsync(execution);

        return execution.Id;
    }
}

