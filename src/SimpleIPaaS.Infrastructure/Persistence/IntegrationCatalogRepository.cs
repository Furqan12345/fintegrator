using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class IntegrationCatalogRepository : IIntegrationCatalogRepository
{
    private readonly IPaaSContext _context;

    public IntegrationCatalogRepository(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<Integration?> GetByIdAsync(Guid id)
    {
        return await _context.Integrations
            .Include(integration => integration.Flows.OrderByDescending(flow => flow.UpdatedAt))
                .ThenInclude(flow => flow.Nodes)
            .Include(integration => integration.Flows)
                .ThenInclude(flow => flow.Edges)
            .FirstOrDefaultAsync(integration => integration.Id == id);
    }

    public async Task<IEnumerable<Integration>> GetAllAsync()
    {
        return await _context.Integrations
            .Include(integration => integration.Flows.OrderByDescending(flow => flow.UpdatedAt))
                .ThenInclude(flow => flow.Nodes)
            .Include(integration => integration.Flows)
                .ThenInclude(flow => flow.Edges)
            .OrderBy(integration => integration.Name)
            .ToListAsync();
    }

    public async Task AddAsync(Integration integration)
    {
        _context.Integrations.Add(integration);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(Integration integration)
    {
        _context.Integrations.Update(integration);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var integration = await GetByIdAsync(id);
        if (integration == null)
        {
            return;
        }

        foreach (var flow in integration.Flows)
        {
            flow.IntegrationId = null;
        }

        _context.Integrations.Remove(integration);
        await _context.SaveChangesAsync();
    }
}
