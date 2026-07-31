using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IIntegrationRepository
{
    Task<IntegrationFlow?> GetByIdAsync(Guid id);
    Task<IEnumerable<IntegrationFlow>> GetAllAsync();
    Task<IEnumerable<IntegrationFlow>> GetByIntegrationIdAsync(Guid integrationId);
    Task AddAsync(IntegrationFlow flow);
    Task<bool> UpdateAsync(IntegrationFlow flow);
    Task DeleteAsync(Guid id);
    Task UpdatePersistedStateAsync(Guid flowId, string persistedStateJson);
    Task<string> GetPersistedStateAsync(Guid flowId);
    Task<bool> UpdateWebhookSecretAsync(Guid flowId, string webhookSecret);
    Task<IntegrationFlow?> GetFlowForTriggerAsync(Guid id);
    Task<IEnumerable<IntegrationFlow>> GetActiveCronFlowsAcrossTenantsAsync();
}

public interface ICronScheduleRepository
{
    Task UpdateNextRunAtAsync(Guid flowId, Guid tenantId, DateTime? nextRunAt);
}
