using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
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
            .OrderByDescending(f => f.UpdatedAt)
            .Include(f => f.Nodes)
            .Include(f => f.Edges)
            .ToListAsync();
    }

    public async Task<IEnumerable<IntegrationFlow>> GetByIntegrationIdAsync(Guid integrationId)
    {
        return await _context.IntegrationFlows
            .Where(flow => flow.IntegrationId == integrationId)
            .OrderByDescending(flow => flow.UpdatedAt)
            .Include(flow => flow.Nodes)
            .Include(flow => flow.Edges)
            .ToListAsync();
    }

    public async Task<IntegrationFlow?> GetByIdAsync(Guid id)
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
            .FirstOrDefaultAsync(f => f.Id == flow.Id && f.TenantId == _tenantContext.TenantId);

        if (existing == null)
        {
            StampTenant(flow, _tenantContext.TenantId);
            _context.IntegrationFlows.Add(flow);
            await _context.SaveChangesAsync();
            return;
        }

        existing.Name = flow.Name;
        existing.Description = flow.Description;
        existing.IntegrationId = flow.IntegrationId;
        existing.Status = flow.Status;
        if (existing.CronExpression != flow.CronExpression || existing.TriggerType != flow.TriggerType)
        {
            existing.NextRunAt = null;
        }
        existing.TriggerType = flow.TriggerType;
        existing.CronExpression = flow.CronExpression;
        existing.WebhookSecret = flow.WebhookSecret;
        existing.PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson;
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
        if (flow == null)
        {
            return;
        }

        var executionIds = await _context.FlowExecutions
            .Where(e => e.FlowId == id)
            .Select(e => e.Id)
            .ToListAsync();

        if (executionIds.Count > 0)
        {
            await _context.DeadLetterEntries
                .Where(d => executionIds.Contains(d.FlowExecutionId))
                .ExecuteDeleteAsync();
            await _context.StepExecutions
                .Where(s => executionIds.Contains(s.FlowExecutionId))
                .ExecuteDeleteAsync();
            await _context.FlowExecutions
                .Where(e => e.FlowId == id)
                .ExecuteDeleteAsync();
        }

        _context.IntegrationFlows.Remove(flow);
        await _context.SaveChangesAsync();
    }

    public async Task UpdatePersistedStateAsync(Guid flowId, string persistedStateJson)
    {
        var flow = await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(existingFlow => existingFlow.Id == flowId && existingFlow.TenantId == _tenantContext.TenantId);

        if (flow == null)
        {
            return;
        }

        flow.PersistedStateJson = string.IsNullOrWhiteSpace(persistedStateJson) ? "{}" : persistedStateJson;
        flow.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task<string> GetPersistedStateAsync(Guid flowId)
    {
        var flow = await _context.IntegrationFlows
            .FirstOrDefaultAsync(existingFlow => existingFlow.Id == flowId);

        return flow?.PersistedStateJson ?? "{}";
    }

    public async Task<IntegrationFlow?> GetFlowForTriggerAsync(Guid id)
    {
        return await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task<IEnumerable<IntegrationFlow>> GetActiveCronFlowsAcrossTenantsAsync()
    {
        return await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.Status == FlowStatus.Active && f.TriggerType == TriggerType.Cron && f.CronExpression != "")
            .ToListAsync();
    }

    public async Task UpdateNextRunAtAsync(Guid flowId, DateTime? nextRunAt)
    {
        var flow = await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == flowId);

        if (flow == null)
        {
            return;
        }

        flow.NextRunAt = nextRunAt;
        await _context.SaveChangesAsync();
    }

    private static void StampTenant(IntegrationFlow flow, Guid tenantId)
    {
        flow.TenantId = tenantId;
        flow.PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson;

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
