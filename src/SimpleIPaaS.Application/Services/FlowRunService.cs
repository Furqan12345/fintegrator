using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public class FlowRunService
{
    private readonly IExecutionRepository _executionRepository;
    private readonly IIntegrationRepository _integrationRepository;
    private readonly IIntegrationCatalogRepository _integrationCatalogRepository;
    private readonly ILogger<FlowRunService> _logger;

    public FlowRunService(IExecutionRepository executionRepository, IIntegrationRepository integrationRepository, IIntegrationCatalogRepository integrationCatalogRepository, ILogger<FlowRunService> logger)
    {
        _executionRepository = executionRepository;
        _integrationRepository = integrationRepository;
        _integrationCatalogRepository = integrationCatalogRepository;
        _logger = logger;
    }

    public async Task<Guid> EnqueueAsync(Guid flowId, Guid tenantId, string triggerSource, string? triggerPayload, CancellationToken cancellationToken = default, bool isTest = false, Guid? testCaseId = null, Guid? testRunId = null, string? testDefinitionJson = null)
    {
        using var activity = new Activity("flow.enqueue");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        var flow = await _integrationRepository.GetByIdAsync(flowId);
        var integrationName = string.Empty;
        if (flow?.IntegrationId is Guid integrationId && integrationId != Guid.Empty)
        {
            var integration = await _integrationCatalogRepository.GetByIdAsync(integrationId);
            integrationName = integration?.Name ?? string.Empty;
        }

        var queuedAt = DateTime.UtcNow;

        var execution = new FlowExecution
        {
            Id = Guid.NewGuid(),
            FlowId = flowId,
            TenantId = tenantId,
            FlowName = flow?.Name ?? string.Empty,
            IntegrationId = flow?.IntegrationId,
            IntegrationName = integrationName,
            TraceParent = activity.Id,
            Status = ExecutionStatus.Queued,
            StartedAt = queuedAt,
            QueuedAt = queuedAt,
            TriggerSource = triggerSource,
            TriggerPayloadJson = triggerPayload,
            IsTest = isTest,
            TestCaseId = testCaseId,
            TestRunId = testRunId,
            TestDefinitionJson = testDefinitionJson
        };

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EventName"] = "flow.execution.queued",
            ["Operation"] = "enqueue",
            ["FlowId"] = flowId,
            ["FlowName"] = execution.FlowName,
            ["IntegrationId"] = execution.IntegrationId,
            ["IntegrationName"] = execution.IntegrationName,
            ["ExecutionId"] = execution.Id,
            ["TenantId"] = tenantId,
            ["IsTest"] = isTest,
            ["TestCaseId"] = testCaseId,
            ["TestRunId"] = testRunId
        });
        await _executionRepository.AddFlowExecutionAsync(execution);
        _logger.LogInformation(new EventId(1100, "flow.execution.queued"), "Queued execution {ExecutionId} for flow {FlowId} from {TriggerSource}", execution.Id, flowId, triggerSource);
        return execution.Id;
    }
}