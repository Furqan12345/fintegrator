using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
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
        // Editing an active flow creates a draft; only PublishAsync can make it runnable again.
        existing.Status = FlowStatus.Draft;
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

    public async Task<IReadOnlyList<FlowVersion>> GetVersionsAsync(Guid flowId)
    {
        return await _context.FlowVersions
            .AsNoTracking()
            .Where(version => version.FlowId == flowId)
            .OrderByDescending(version => version.VersionNumber)
            .ToListAsync();
    }

    public async Task<FlowVersion?> PublishAsync(Guid flowId, string? changeNote = null)
    {
        var flow = await LoadMutableFlowAsync(flowId);
        if (flow == null)
        {
            return null;
        }

        var versionNumber = await NextVersionNumberAsync(flowId);
        var now = DateTime.UtcNow;
        flow.Status = FlowStatus.Active;
        flow.PublishedVersion = versionNumber;
        flow.LastPublishedAt = now;
        flow.UpdatedAt = now;
        SyncOneTimeSchedule(flow);

        var version = new FlowVersion
        {
            FlowId = flow.Id,
            TenantId = flow.TenantId,
            VersionNumber = versionNumber,
            CreatedAt = now,
            ChangeNote = NormalizeChangeNote(changeNote),
            SnapshotJson = SerializeSnapshot(flow)
        };

        _context.FlowVersions.Add(version);
        await _context.SaveChangesAsync();
        return version;
    }

    public async Task<IntegrationFlow?> RollbackAsync(Guid flowId, int versionNumber, string? changeNote = null)
    {
        if (versionNumber < 1)
        {
            return null;
        }

        var flow = await LoadMutableFlowAsync(flowId);
        var target = await _context.FlowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(version => version.FlowId == flowId && version.VersionNumber == versionNumber);

        if (flow == null || target == null)
        {
            return null;
        }

        FlowSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<FlowSnapshot>(target.SnapshotJson, JsonOptions)
                ?? throw new JsonException("The stored flow version is empty.");
        }
        catch (JsonException)
        {
            return null;
        }

        ApplySnapshot(flow, snapshot);
        var newVersionNumber = await NextVersionNumberAsync(flowId);
        var now = DateTime.UtcNow;
        flow.Status = FlowStatus.Active;
        flow.PublishedVersion = newVersionNumber;
        flow.LastPublishedAt = now;
        flow.UpdatedAt = now;
        SyncOneTimeSchedule(flow);

        _context.FlowVersions.Add(new FlowVersion
        {
            FlowId = flow.Id,
            TenantId = flow.TenantId,
            VersionNumber = newVersionNumber,
            CreatedAt = now,
            ChangeNote = string.IsNullOrWhiteSpace(changeNote)
                ? $"Rollback to version {versionNumber}"
                : NormalizeChangeNote(changeNote),
            RolledBackFromVersion = versionNumber,
            SnapshotJson = SerializeSnapshot(flow)
        });

        await _context.SaveChangesAsync();
        return flow;
    }

    private async Task<IntegrationFlow?> LoadMutableFlowAsync(Guid flowId)
    {
        return await _context.IntegrationFlows
            .IgnoreQueryFilters()
            .Include(flow => flow.Nodes)
            .Include(flow => flow.Edges)
            .FirstOrDefaultAsync(flow => flow.Id == flowId && flow.TenantId == _tenantContext.TenantId);
    }

    private async Task<int> NextVersionNumberAsync(Guid flowId)
    {
        return (await _context.FlowVersions
            .IgnoreQueryFilters()
            .Where(version => version.FlowId == flowId && version.TenantId == _tenantContext.TenantId)
            .Select(version => (int?)version.VersionNumber)
            .MaxAsync() ?? 0) + 1;
    }

    private static string NormalizeChangeNote(string? changeNote)
    {
        return string.IsNullOrWhiteSpace(changeNote) ? "Published from draft" : changeNote.Trim();
    }

    private static string SerializeSnapshot(IntegrationFlow flow)
    {
        var snapshot = new FlowSnapshot(
            flow.Name,
            flow.Description,
            flow.IntegrationId,
            flow.PersistedStateJson,
            flow.TriggerType,
            flow.CronExpression,
            flow.WebhookSecret,
            flow.RunAt,
            flow.AllowPostReplay,
            flow.Nodes.Select(CloneNode).ToList(),
            flow.Edges.Select(CloneEdge).ToList());

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private void ApplySnapshot(IntegrationFlow flow, FlowSnapshot snapshot)
    {
        flow.Name = snapshot.Name;
        flow.Description = snapshot.Description;
        flow.IntegrationId = snapshot.IntegrationId;
        flow.PersistedStateJson = string.IsNullOrWhiteSpace(snapshot.PersistedStateJson) ? "{}" : snapshot.PersistedStateJson;
        flow.TriggerType = snapshot.TriggerType;
        flow.CronExpression = snapshot.CronExpression;
        flow.WebhookSecret = snapshot.WebhookSecret;
        flow.RunAt = snapshot.RunAt;
        flow.AllowPostReplay = snapshot.AllowPostReplay;

        var incomingNodeIds = snapshot.Nodes.Select(node => node.Id).ToHashSet();
        var incomingEdgeIds = snapshot.Edges.Select(edge => edge.Id).ToHashSet();
        var existingNodes = flow.Nodes.ToDictionary(node => node.Id);
        var existingEdges = flow.Edges.ToDictionary(edge => edge.Id);

        // The caller's DbContext tracks these entities; update matching rows and
        // delete only rows absent from the restored snapshot.
        // Child entities are re-stamped below to the current flow and tenant.
        foreach (var node in flow.Nodes.Where(node => !incomingNodeIds.Contains(node.Id)).ToList())
        {
            _context.IntegrationSteps.Remove(node);
        }

        foreach (var edge in flow.Edges.Where(edge => !incomingEdgeIds.Contains(edge.Id)).ToList())
        {
            _context.IntegrationEdges.Remove(edge);
        }

        foreach (var node in snapshot.Nodes)
        {
            node.FlowId = flow.Id;
            node.TenantId = flow.TenantId;
            if (existingNodes.TryGetValue(node.Id, out var storedNode))
            {
                ApplyNodeExact(storedNode, node);
            }
            else
            {
                flow.Nodes.Add(node);
            }
        }

        foreach (var edge in snapshot.Edges)
        {
            edge.FlowId = flow.Id;
            edge.TenantId = flow.TenantId;
            if (existingEdges.TryGetValue(edge.Id, out var storedEdge))
            {
                ApplyEdge(storedEdge, edge);
            }
            else
            {
                flow.Edges.Add(edge);
            }
        }
    }

    private static IntegrationStep CloneNode(IntegrationStep source) => new()
    {
        Id = source.Id,
        TenantId = source.TenantId,
        FlowId = source.FlowId,
        StepType = source.StepType,
        PositionX = source.PositionX,
        PositionY = source.PositionY,
        EndpointUrl = source.EndpointUrl,
        HttpMethod = source.HttpMethod,
        UrlMode = source.UrlMode,
        AuthType = source.AuthType,
        AuthToken = source.AuthToken,
        AuthUsername = source.AuthUsername,
        AuthPassword = source.AuthPassword,
        AuthConfigJson = source.AuthConfigJson,
        ConnectionId = source.ConnectionId,
        MappingCode = source.MappingCode,
        StepConfig = source.StepConfig,
        NodeName = source.NodeName,
        UrlCode = source.UrlCode,
        PreFlightCode = source.PreFlightCode,
        PostFlightCode = source.PostFlightCode
    };

    private static IntegrationEdge CloneEdge(IntegrationEdge source) => new()
    {
        Id = source.Id,
        TenantId = source.TenantId,
        FlowId = source.FlowId,
        SourceNodeId = source.SourceNodeId,
        TargetNodeId = source.TargetNodeId,
        SourcePortId = source.SourcePortId,
        TargetPortId = source.TargetPortId,
        Condition = source.Condition,
        Order = source.Order
    };

    private static void ApplyNodeExact(IntegrationStep target, IntegrationStep source)
    {
        target.StepType = source.StepType;
        target.NodeName = source.NodeName;
        target.PositionX = source.PositionX;
        target.PositionY = source.PositionY;
        target.EndpointUrl = source.EndpointUrl;
        target.HttpMethod = source.HttpMethod;
        target.UrlMode = source.UrlMode;
        target.AuthType = source.AuthType;
        target.AuthToken = source.AuthToken;
        target.AuthUsername = source.AuthUsername;
        target.AuthPassword = source.AuthPassword;
        target.AuthConfigJson = source.AuthConfigJson;
        target.ConnectionId = source.ConnectionId;
        target.MappingCode = source.MappingCode;
        target.UrlCode = source.UrlCode;
        target.PreFlightCode = source.PreFlightCode;
        target.PostFlightCode = source.PostFlightCode;
        target.StepConfig = source.StepConfig;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record FlowSnapshot(
        string Name,
        string Description,
        Guid? IntegrationId,
        string PersistedStateJson,
        TriggerType TriggerType,
        string CronExpression,
        string WebhookSecret,
        DateTime? RunAt,
        bool AllowPostReplay,
        List<IntegrationStep> Nodes,
        List<IntegrationEdge> Edges);
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
            .FirstOrDefaultAsync(f => f.Id == id && f.Status == FlowStatus.Active);
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
