using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IIntegrationCatalogRepository
{
    Task<Integration?> GetByIdAsync(Guid id);
    Task<IEnumerable<Integration>> GetAllAsync();

    // Catalog view for list pages: integrations with their flows, and each flow's non-archived steps reduced to
    // id, name and type. No scripts, step configuration, edges, helper code or persisted state is loaded.
    Task<IEnumerable<Integration>> GetAllSummariesAsync();
    Task AddAsync(Integration integration);
    Task UpdateAsync(Integration integration);
    Task DeleteAsync(Guid id);
}
