using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class IntegrationRepository : IIntegrationRepository
{
    private readonly IPaaSContext _context;
    private readonly ITenantContext _tenantContext;

    public IntegrationRepository(IPaaSContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
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
        StampTenant(flow, _tenantContext.TenantId);
        _context.IntegrationFlows.Add(flow);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(IntegrationFlow flow)
    {
        var existing = await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .Include(f => f.Nodes)
            .Include(f => f.Edges)
            .FirstOrDefaultAsync(f => f.Id == flow.Id);

        if (existing == null)
        {
            StampTenant(flow, _tenantContext.TenantId);
            _context.IntegrationFlows.Add(flow);
            await _context.SaveChangesAsync();
            return;
        }

        existing.Name = flow.Name;
        existing.Description = flow.Description;
        existing.Status = flow.Status;
        existing.TriggerType = flow.TriggerType;
        existing.CronExpression = flow.CronExpression;
        existing.WebhookSecret = flow.WebhookSecret;
        existing.TenantId = existing.TenantId == Guid.Empty ? _tenantContext.TenantId : existing.TenantId;
        existing.UpdatedAt = DateTime.UtcNow;

        _context.IntegrationSteps.RemoveRange(existing.Nodes);
        _context.IntegrationEdges.RemoveRange(existing.Edges);

        StampTenant(flow, existing.TenantId);
        foreach (var node in flow.Nodes)
        {
            node.FlowId = existing.Id;
            _context.IntegrationSteps.Add(node);
        }

        foreach (var edge in flow.Edges)
        {
            edge.FlowId = existing.Id;
            _context.IntegrationEdges.Add(edge);
        }

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

    private static void StampTenant(IntegrationFlow flow, Guid tenantId)
    {
        flow.TenantId = tenantId;

        foreach (var node in flow.Nodes)
        {
            node.FlowId = flow.Id;
            node.TenantId = tenantId;
        }

        foreach (var edge in flow.Edges)
        {
            edge.FlowId = flow.Id;
            edge.TenantId = tenantId;
        }
    }
}
