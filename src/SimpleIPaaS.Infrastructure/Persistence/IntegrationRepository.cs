using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class IntegrationRepository : IIntegrationRepository
{
    private readonly IPaaSContext _context;

    public IntegrationRepository(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<IntegrationFlow>> GetAllAsync()
    {
        return await _context.IntegrationFlows
            .Include(f => f.Nodes)
            .Include(f => f.Edges)
            .ToListAsync();
    }

    public async Task<IntegrationFlow> GetByIdAsync(Guid id)
    {
        return await _context.IntegrationFlows
            .Include(f => f.Nodes)
            .Include(f => f.Edges)
            .FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task AddAsync(IntegrationFlow flow)
    {
        _context.IntegrationFlows.Add(flow);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(IntegrationFlow flow)
    {
        _context.IntegrationFlows.Update(flow);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var flow = await GetByIdAsync(id);
        if (flow != null)
        {
            _context.IntegrationFlows.Remove(flow);
            await _context.SaveChangesAsync();
        }
    }
}
