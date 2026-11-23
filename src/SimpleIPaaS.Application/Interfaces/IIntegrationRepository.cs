using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IIntegrationRepository
{
    Task<IntegrationFlow?> GetByIdAsync(Guid id);
    Task<IEnumerable<IntegrationFlow>> GetAllAsync();

    // List-page view of every flow: scalar fields plus the non-archived steps reduced to id, name and type. No
    // scripts, step configuration, edges, helper code or persisted state is loaded.
    Task<IEnumerable<IntegrationFlow>> GetAllSummariesAsync();
    Task<IEnumerable<IntegrationFlow>> GetByIntegrationIdAsync(Guid integrationId);
    Task AddAsync(IntegrationFlow flow);
    Task<bool> UpdateAsync(IntegrationFlow flow);
    Task<IReadOnlyList<FlowVersion>> GetVersionsAsync(Guid flowId);
    Task<FlowVersion?> PublishAsync(Guid flowId, string? changeNote = null);
    Task<IntegrationFlow?> RollbackAsync(Guid flowId, int versionNumber, string? changeNote = null);
    Task DeleteAsync(Guid id);
    Task UpdatePersistedStateAsync(Guid flowId, string persistedStateJson);
    Task<string> GetPersistedStateAsync(Guid flowId);
    Task<bool> UpdateWebhookSecretAsync(Guid flowId, string webhookSecret);
    Task<IntegrationFlow?> GetFlowForTriggerAsync(Guid id);
    Task<IEnumerable<IntegrationFlow>> GetActiveCronFlowsAsync();
    Task<IntegrationFlow?> SetScheduleStatusAsync(Guid flowId, FlowStatus status);
}

public interface ICronScheduleRepository
{
    Task UpdateNextRunAtAsync(Guid flowId, DateTime? nextRunAt);
}
