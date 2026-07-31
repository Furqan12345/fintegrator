using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class IntegrationRepository : IIntegrationRepository, ICronScheduleRepository
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

    public async Task<bool> UpdateAsync(IntegrationFlow flow)
    {
        var existing = await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .Include(f => f.Nodes)
            .Include(f => f.Edges)
            .FirstOrDefaultAsync(f => f.Id == flow.Id && f.TenantId == _tenantContext.TenantId);

        if (existing == null)
        {
            return false;
        }

        existing.Name = flow.Name;
        existing.Description = flow.Description;
        existing.IntegrationId = flow.IntegrationId;
        existing.Status = flow.Status;
        if (existing.CronExpression != flow.CronExpression || existing.TriggerType != flow.TriggerType || existing.RunAt != flow.RunAt)
        {
            existing.NextRunAt = null;
        }
        existing.TriggerType = flow.TriggerType;
        existing.CronExpression = flow.CronExpression;
        existing.RunAt = flow.RunAt;
        SyncOneTimeSchedule(existing);
        existing.WebhookSecret = string.IsNullOrWhiteSpace(existing.WebhookSecret) ? flow.WebhookSecret : existing.WebhookSecret;
        existing.AllowPostReplay = flow.AllowPostReplay;
        existing.PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson;
        existing.TenantId = existing.TenantId == Guid.Empty ? _tenantContext.TenantId : existing.TenantId;
        existing.UpdatedAt = DateTime.UtcNow;

        var incomingNodeIds = flow.Nodes.Select(node => node.Id).ToHashSet();
        var incomingEdgeIds = flow.Edges.Select(edge => edge.Id).ToHashSet();

        var existingNodes = existing.Nodes.ToDictionary(node => node.Id);
        var existingEdges = existing.Edges.ToDictionary(edge => edge.Id);

        _context.IntegrationSteps.RemoveRange(existing.Nodes.Where(node => !incomingNodeIds.Contains(node.Id)));
        _context.IntegrationEdges.RemoveRange(existing.Edges.Where(edge => !incomingEdgeIds.Contains(edge.Id)));

        foreach (var node in flow.Nodes)
        {
            node.FlowId = existing.Id;
            node.TenantId = existing.TenantId;

            if (existingNodes.TryGetValue(node.Id, out var storedNode))
            {
                ApplyNode(storedNode, node);
                continue;
            }

            _context.IntegrationSteps.Add(node);
        }

        foreach (var edge in flow.Edges)
        {
            edge.FlowId = existing.Id;
            edge.TenantId = existing.TenantId;

            if (existingEdges.TryGetValue(edge.Id, out var storedEdge))
            {
                ApplyEdge(storedEdge, edge);
                continue;
            }

            _context.IntegrationEdges.Add(edge);
        }

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateWebhookSecretAsync(Guid flowId, string webhookSecret)
    {
        var flow = await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(existingFlow => existingFlow.Id == flowId && existingFlow.TenantId == _tenantContext.TenantId);

        if (flow == null)
        {
            return false;
        }

        flow.WebhookSecret = webhookSecret;
        flow.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
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
            .Where(f => f.Status == FlowStatus.Active && f.TriggerType == TriggerType.Cron && (f.CronExpression != "" || f.RunAt != null))
            .ToListAsync();
    }

    public async Task UpdateNextRunAtAsync(Guid flowId, Guid tenantId, DateTime? nextRunAt)
    {
        if (tenantId == Guid.Empty)
        {
            return;
        }

        var flow = await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == flowId && f.TenantId == tenantId);

        if (flow == null)
        {
            return;
        }

        flow.NextRunAt = nextRunAt;
        await _context.SaveChangesAsync();
    }

    private static void ApplyNode(IntegrationStep target, IntegrationStep source)
    {
        target.StepType = source.StepType;
        target.NodeName = source.NodeName;
        target.PositionX = source.PositionX;
        target.PositionY = source.PositionY;
        target.EndpointUrl = source.EndpointUrl;
        target.HttpMethod = source.HttpMethod;
        target.UrlMode = source.UrlMode;
        target.AuthType = source.AuthType;
        target.AuthToken = KeepStoredWhenEmpty(source.AuthToken, target.AuthToken);
        target.AuthUsername = KeepStoredWhenEmpty(source.AuthUsername, target.AuthUsername);
        target.AuthPassword = KeepStoredWhenEmpty(source.AuthPassword, target.AuthPassword);
        target.AuthConfigJson = KeepStoredWhenEmpty(source.AuthConfigJson, target.AuthConfigJson);
        target.ConnectionId = source.ConnectionId;
        target.MappingCode = source.MappingCode;
        target.UrlCode = source.UrlCode;
        target.PreFlightCode = source.PreFlightCode;
        target.PostFlightCode = source.PostFlightCode;
        target.StepConfig = source.StepConfig;
    }

    private static void ApplyEdge(IntegrationEdge target, IntegrationEdge source)
    {
        target.SourceNodeId = source.SourceNodeId;
        target.TargetNodeId = source.TargetNodeId;
        target.SourcePortId = source.SourcePortId;
        target.TargetPortId = source.TargetPortId;
        target.Condition = source.Condition;
        target.Order = source.Order;
    }

    private static string KeepStoredWhenEmpty(string incoming, string stored)
    {
        return string.IsNullOrEmpty(incoming) ? stored : incoming;
    }

    private static void SyncOneTimeSchedule(IntegrationFlow flow)
    {
        if (flow.RunAt == null)
        {
            return;
        }

        flow.NextRunAt = flow.RunAt > DateTime.UtcNow ? flow.RunAt : null;
    }

    private static void StampTenant(IntegrationFlow flow, Guid tenantId)
    {
        flow.TenantId = tenantId;
        flow.PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson;
        SyncOneTimeSchedule(flow);

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
