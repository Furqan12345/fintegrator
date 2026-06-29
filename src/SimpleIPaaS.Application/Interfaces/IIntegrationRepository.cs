using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IIntegrationRepository
{
    Task<IntegrationFlow> GetByIdAsync(Guid id);
    Task<IEnumerable<IntegrationFlow>> GetAllAsync();
    Task AddAsync(IntegrationFlow flow);
    Task UpdateAsync(IntegrationFlow flow);
    Task DeleteAsync(Guid id);
}
