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
            .AsSplitQuery()
            .FirstOrDefaultAsync(integration => integration.Id == id);
    }

    public async Task<IEnumerable<Integration>> GetAllAsync()
    {
        return await _context.Integrations
            .Include(integration => integration.Flows.OrderByDescending(flow => flow.UpdatedAt))
                .ThenInclude(flow => flow.Nodes)
            .Include(integration => integration.Flows)
                .ThenInclude(flow => flow.Edges)
            .AsSplitQuery()
            .OrderBy(integration => integration.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<Integration>> GetAllSummariesAsync()
    {
        // Seven list pages only count a flow's steps and look for a Schedule step, yet the full query pulled every
        // script and edge of every flow (about 440 KB of JSON for four integrations). This projects just what a
        // list shows.
        return await _context.Integrations
            .AsNoTracking()
            .OrderBy(integration => integration.Name)
            .Select(integration => new Integration
            {
                Id = integration.Id,
                Name = integration.Name,
                Description = integration.Description,
                CreatedAt = integration.CreatedAt,
                UpdatedAt = integration.UpdatedAt,
                Flows = integration.Flows
                    .OrderByDescending(flow => flow.UpdatedAt)
                    .Select(flow => new IntegrationFlow
                    {
                        Id = flow.Id,
                        IntegrationId = flow.IntegrationId,
                        Name = flow.Name,
                        Description = flow.Description,
                        Status = flow.Status,
                        TriggerType = flow.TriggerType,
                        CronExpression = flow.CronExpression,
                        RunAt = flow.RunAt,
                        NextRunAt = flow.NextRunAt,
                        AllowPostReplay = flow.AllowPostReplay,
                        DefaultDeadLetterResyncStepId = flow.DefaultDeadLetterResyncStepId,
                        PublishedVersion = flow.PublishedVersion,
                        LastPublishedAt = flow.LastPublishedAt,
                        CreatedAt = flow.CreatedAt,
                        UpdatedAt = flow.UpdatedAt,
                        Nodes = flow.Nodes
                            .Where(node => !node.IsArchived)
                            .Select(node => new IntegrationStep
                            {
                                Id = node.Id,
                                FlowId = node.FlowId,
                                StepType = node.StepType,
                                NodeName = node.NodeName
                            })
                            .ToList()
                    })
                    .ToList()
            })
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
