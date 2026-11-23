using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class ConnectionRepository : IConnectionRepository
{
    private readonly IPaaSContext _context;

    public ConnectionRepository(IPaaSContext context)
    {
        _context = context;
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
