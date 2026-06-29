using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class ConnectionRepository : IConnectionRepository
{
    private readonly IPaaSContext _context;
    private readonly ITenantContext _tenantContext;

    public ConnectionRepository(IPaaSContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<Connection?> GetByIdAsync(Guid id)
    {
        return await _context.Connections
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<IEnumerable<Connection>> GetAllAsync()
    {
        return await _context.Connections.ToListAsync();
    }

    public async Task AddAsync(Connection connection)
    {
        connection.TenantId = _tenantContext.TenantId;
        _context.Connections.Add(connection);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Connection connection)
    {
        _context.Connections.Update(connection);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var connection = await GetByIdAsync(id);
        if (connection != null)
        {
            _context.Connections.Remove(connection);
            await _context.SaveChangesAsync();
        }
    }
}
