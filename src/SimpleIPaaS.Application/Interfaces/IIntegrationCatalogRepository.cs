using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IIntegrationCatalogRepository
{
    Task<Integration?> GetByIdAsync(Guid id);
    Task<IEnumerable<Integration>> GetAllAsync();
    Task AddAsync(Integration integration);
    Task UpdateAsync(Integration integration);
    Task DeleteAsync(Guid id);
}
