using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IConnectionRepository
{
    Task<Connection?> GetByIdAsync(Guid id);
    Task<IEnumerable<Connection>> GetAllAsync();
    Task AddAsync(Connection connection);
    Task UpdateAsync(Connection connection);
    Task DeleteAsync(Guid id);
}
