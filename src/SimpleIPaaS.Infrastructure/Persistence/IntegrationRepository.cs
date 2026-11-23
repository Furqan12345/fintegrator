using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class IntegrationRepository : IIntegrationRepository, ICronScheduleRepository
{
    private readonly IPaaSContext _context;

    public IntegrationRepository(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<IntegrationFlow>> GetAllAsync()
    {
        return await _context.IntegrationFlows
            .OrderByDescending(f => f.UpdatedAt)
            .Include(f => f.Nodes.Where(node => !node.IsArchived))
            .Include(f => f.Edges)
            .ToListAsync();
    }

    public async Task<IEnumerable<IntegrationFlow>> GetAllSummariesAsync()
    {
        return await _context.IntegrationFlows
            .AsNoTracking()
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
            .ToListAsync();
    }

    public async Task<IEnumerable<IntegrationFlow>> GetByIntegrationIdAsync(Guid integrationId)
    {
        return await _context.IntegrationFlows
            .Where(flow => flow.IntegrationId == integrationId)
            .OrderByDescending(flow => flow.UpdatedAt)
            .Include(flow => flow.Nodes.Where(node => !node.IsArchived))
            .Include(flow => flow.Edges)
            .ToListAsync();
    }

    public async Task<IntegrationFlow?> GetByIdAsync(Guid id)
    {
        return await _context.IntegrationFlows
            .Include(f => f.Nodes.Where(node => !node.IsArchived))
            .Include(f => f.Edges)
            .FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task AddAsync(IntegrationFlow flow)
    {
        await RemapForeignIdsAsync(flow);
        if (flow.DefaultDeadLetterResyncStepId is Guid selectedNodeId && !flow.Nodes.Any(node => node.Id == selectedNodeId))
        {
            flow.DefaultDeadLetterResyncStepId = null;
        }
        flow.PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson;
        SyncOneTimeSchedule(flow);
        foreach (var node in flow.Nodes)
        {
            node.FlowId = flow.Id;
        }

        foreach (var edge in flow.Edges)
        {
            edge.FlowId = flow.Id;
        }

        _context.IntegrationFlows.Add(flow);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> UpdateAsync(IntegrationFlow flow)
    {
        var existing = await _context.IntegrationFlows
            .Include(f => f.Nodes.Where(node => !node.IsArchived))
            .Include(f => f.Edges)
            .FirstOrDefaultAsync(f => f.Id == flow.Id);

        if (existing == null)
        {
            return false;
        }

        await RemapForeignIdsAsync(flow);
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
        existing.DefaultDeadLetterResyncStepId = flow.DefaultDeadLetterResyncStepId;
        existing.PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson;
        existing.HelperCode = flow.HelperCode ?? string.Empty;
        existing.UpdatedAt = DateTime.UtcNow;

        var incomingNodeIds = flow.Nodes.Select(node => node.Id).ToHashSet();
        if (existing.DefaultDeadLetterResyncStepId is Guid selectedNodeId && !incomingNodeIds.Contains(selectedNodeId))
        {
            existing.DefaultDeadLetterResyncStepId = null;
        }
        var incomingEdgeIds = flow.Edges.Select(edge => edge.Id).ToHashSet();

        var activeExistingNodes = existing.Nodes.Where(node => !node.IsArchived).ToList();
        var existingNodes = activeExistingNodes.ToDictionary(node => node.Id);
        var existingEdges = existing.Edges.ToDictionary(edge => edge.Id);
        var removedNodes = activeExistingNodes.Where(node => !incomingNodeIds.Contains(node.Id)).ToList();
        await PreserveStepHistoryAsync(removedNodes);
        _context.IntegrationSteps.RemoveRange(removedNodes.Where(node => !node.IsArchived));
        _context.IntegrationEdges.RemoveRange(existing.Edges.Where(edge => !incomingEdgeIds.Contains(edge.Id)));

        foreach (var node in flow.Nodes)
        {
            node.FlowId = existing.Id;

            if (existingNodes.TryGetValue(node.Id, out var storedNode))
            {
                storedNode.IsArchived = false;
                ApplyNode(storedNode, node);
                continue;
            }

            _context.IntegrationSteps.Add(node);
        }

        foreach (var edge in flow.Edges)
        {
            edge.FlowId = existing.Id;

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

    // Node and edge IDs are globally unique PKs. AI drafts (and pasted graphs) can carry
    // IDs copied from other flows, which crashed saves with "UNIQUE constraint failed:
    // IntegrationSteps.Id". Any incoming ID that already exists outside this flow's active
    // graph is regenerated, and every graph reference is remapped to match.
    private async Task RemapForeignIdsAsync(IntegrationFlow flow)
    {
        var nodeIds = flow.Nodes.Select(node => node.Id).ToList();
        var edgeIds = flow.Edges.Select(edge => edge.Id).ToList();
        var collidingNodeIds = await _context.IntegrationSteps.IgnoreQueryFilters()
            .Where(node => nodeIds.Contains(node.Id) && (node.FlowId != flow.Id || node.IsArchived))
            .Select(node => node.Id)
            .ToListAsync();
        var collidingEdgeIds = await _context.IntegrationEdges
            .Where(edge => edgeIds.Contains(edge.Id) && edge.FlowId != flow.Id)
            .Select(edge => edge.Id)
            .ToListAsync();
        if (collidingNodeIds.Count == 0 && collidingEdgeIds.Count == 0) return;

        var nodeMap = new Dictionary<Guid, Guid>();
        foreach (var node in flow.Nodes.Where(node => collidingNodeIds.Contains(node.Id)))
        {
            var newId = Guid.NewGuid();
            nodeMap[node.Id] = newId;
            node.Id = newId;
        }
        foreach (var edge in flow.Edges)
        {
            if (nodeMap.TryGetValue(edge.SourceNodeId, out var sourceId)) edge.SourceNodeId = sourceId;
            if (nodeMap.TryGetValue(edge.TargetNodeId, out var targetId)) edge.TargetNodeId = targetId;
        }
        foreach (var edge in flow.Edges.Where(edge => collidingEdgeIds.Contains(edge.Id)))
        {
            edge.Id = Guid.NewGuid();
        }
        if (flow.DefaultDeadLetterResyncStepId is Guid resyncNodeId && nodeMap.TryGetValue(resyncNodeId, out var mappedResyncId))
        {
            flow.DefaultDeadLetterResyncStepId = mappedResyncId;
        }
    }

    private async Task PreserveStepHistoryAsync(IReadOnlyCollection<IntegrationStep> nodes)
    {
        if (nodes.Count == 0) return;

        var nodeIds = nodes.Select(node => node.Id).ToArray();
        var referencedIds = (await _context.StepExecutions
                .Where(execution => nodeIds.Contains(execution.StepId))
                .Select(execution => execution.StepId)
                .Distinct()
                .ToListAsync())
            .Concat(await _context.DeadLetterEntries
                .Where(entry => nodeIds.Contains(entry.StepId))
                .Select(entry => entry.StepId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        foreach (var node in nodes.Where(node => referencedIds.Contains(node.Id)))
        {
            node.IsArchived = true;
        }
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

        // Publishing content that is identical to the newest stored snapshot must not mint a version.
        // Publishing is reachable from several surfaces and a click can repeat, and a rollback
        // restores a snapshot exactly, so "publish again" used to append a row carrying no change.
        // Reactivation still happens, because a paused schedule must go live even when the graph
        // itself did not change.
        var latest = await _context.FlowVersions
            .AsNoTracking()
            .Where(version => version.FlowId == flowId)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync();

        if (latest != null && IsSameSnapshot(flow, latest.SnapshotJson))
        {
            var reactivated = flow.Status != FlowStatus.Active || flow.PublishedVersion != latest.VersionNumber;
            if (reactivated)
            {
                flow.Status = FlowStatus.Active;
                flow.PublishedVersion = latest.VersionNumber;
                flow.LastPublishedAt ??= latest.CreatedAt;
                flow.UpdatedAt = DateTime.UtcNow;
                SyncOneTimeSchedule(flow);
                await _context.SaveChangesAsync();
            }

            return latest;
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
            VersionNumber = versionNumber,
            CreatedAt = now,
            ChangeNote = NormalizeChangeNote(changeNote),
            SnapshotJson = SerializeSnapshot(flow)
        };

        _context.FlowVersions.Add(version);
        await _context.SaveChangesAsync();
        return version;
    }

    // Compares the live flow with a stored snapshot order-insensitively: EF Include does not
    // guarantee node/edge ordering, so a raw string comparison would report spurious differences
    // (and occasionally miss a real one) on identical content.
    private static bool IsSameSnapshot(IntegrationFlow flow, string snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return false;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<FlowSnapshot>(snapshotJson, JsonOptions);
            if (stored == null)
            {
                return false;
            }

            return CanonicalSnapshotJson(BuildSnapshot(flow)) == CanonicalSnapshotJson(stored);
        }
        catch (JsonException)
        {
            // An unreadable historical snapshot cannot prove "nothing changed", so publish normally.
            return false;
        }
    }

    private static string CanonicalSnapshotJson(FlowSnapshot snapshot)
    {
        var canonical = snapshot with
        {
            Nodes = snapshot.Nodes.OrderBy(node => node.Id).ToList(),
            Edges = snapshot.Edges.OrderBy(edge => edge.Id).ToList()
        };

        return JsonSerializer.Serialize(canonical, JsonOptions);
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

        await ApplySnapshotAsync(flow, snapshot);
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
        var flow = await _context.IntegrationFlows
            .Include(item => item.Nodes.Where(node => !node.IsArchived))
            .Include(item => item.Edges)
            .FirstOrDefaultAsync(item => item.Id == flowId);
        if (flow == null) return null;

        // Archived nodes are loaded on purpose: an edit must archive, not drop, history-referenced rows.
        flow.Nodes = await _context.IntegrationSteps.IgnoreQueryFilters()
            .Where(node => node.FlowId == flowId)
            .ToListAsync();
        return flow;
    }

    private async Task<int> NextVersionNumberAsync(Guid flowId)
    {
        return (await _context.FlowVersions
            .Where(version => version.FlowId == flowId)
            .Select(version => (int?)version.VersionNumber)
            .MaxAsync() ?? 0) + 1;
    }

    private static string NormalizeChangeNote(string? changeNote)
    {
        return string.IsNullOrWhiteSpace(changeNote) ? "Published from draft" : changeNote.Trim();
    }

    private static FlowSnapshot BuildSnapshot(IntegrationFlow flow) => new(
        flow.Name,
        flow.Description,
        flow.IntegrationId,
        flow.PersistedStateJson,
        flow.TriggerType,
        flow.CronExpression,
        flow.WebhookSecret,
        flow.RunAt,
        flow.AllowPostReplay,
        flow.Nodes.Where(node => !node.IsArchived).Select(CloneNode).ToList(),
        flow.Edges.Select(CloneEdge).ToList(),
        flow.DefaultDeadLetterResyncStepId,
        flow.HelperCode);

    private static string SerializeSnapshot(IntegrationFlow flow)
    {
        return JsonSerializer.Serialize(BuildSnapshot(flow), JsonOptions);
    }

    private async Task ApplySnapshotAsync(IntegrationFlow flow, FlowSnapshot snapshot)
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
        flow.DefaultDeadLetterResyncStepId = snapshot.DefaultDeadLetterResyncStepId;
        flow.HelperCode = snapshot.HelperCode ?? string.Empty;

        var incomingNodeIds = snapshot.Nodes.Select(node => node.Id).ToHashSet();
        var incomingEdgeIds = snapshot.Edges.Select(edge => edge.Id).ToHashSet();
        var existingNodes = flow.Nodes.ToDictionary(node => node.Id);
        var existingEdges = flow.Edges.ToDictionary(edge => edge.Id);

        var removedNodes = flow.Nodes.Where(node => !incomingNodeIds.Contains(node.Id)).ToList();
        await PreserveStepHistoryAsync(removedNodes);
        _context.IntegrationSteps.RemoveRange(removedNodes.Where(node => !node.IsArchived));
        foreach (var node in flow.Nodes.Where(node => node.IsArchived && incomingNodeIds.Contains(node.Id)))
        {
            node.IsArchived = false;
        }

        foreach (var edge in flow.Edges.Where(edge => !incomingEdgeIds.Contains(edge.Id)).ToList())
        {
            _context.IntegrationEdges.Remove(edge);
        }

        foreach (var node in snapshot.Nodes)
        {
            node.FlowId = flow.Id;
            if (existingNodes.TryGetValue(node.Id, out var storedNode))
            {
                storedNode.IsArchived = false;
                ApplyNodeExact(storedNode, node);
            }
            else
            {
                // The snapshot row no longer exists (it was deleted while the graph was edited),
                // so it has to be INSERTed. Adding it to the tracked navigation alone is not
                // enough: the deserialised instance already carries its primary key, and EF's
                // graph attach then treats it as an existing row and issues an UPDATE for a row
                // that is gone — DbUpdateConcurrencyException, surfaced as HTTP 500 on rollback.
                AttachRestoredRow(flow.Nodes, node);
            }
        }

        foreach (var edge in snapshot.Edges)
        {
            edge.FlowId = flow.Id;
            if (existingEdges.TryGetValue(edge.Id, out var storedEdge))
            {
                ApplyEdge(storedEdge, edge);
            }
            else
            {
                AttachRestoredRow(flow.Edges, edge);
            }
        }
    }

    // Explicitly adds a restored snapshot row to the context so EF emits an INSERT, then keeps
    // the navigation collection consistent. Already-tracked ids are added to the collection
    // only, so a caller can never end up tracking two instances with the same key.
    private void AttachRestoredRow<TEntity>(ICollection<TEntity> navigation, TEntity entity) where TEntity : class
    {
        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Detached)
        {
            _context.Add(entity);
        }

        navigation.Add(entity);
    }

    private static IntegrationStep CloneNode(IntegrationStep source) => new()
    {
        Id = source.Id,
        FlowId = source.FlowId,
        StepType = source.StepType,
        IsArchived = false,
        PositionX = source.PositionX,
        PositionY = source.PositionY,
        EndpointUrl = source.EndpointUrl,
        HttpMethod = source.HttpMethod,
        UrlMode = source.UrlMode,
        HttpErrorMode = source.HttpErrorMode,
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
        target.HttpErrorMode = source.HttpErrorMode;
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
        List<IntegrationEdge> Edges,
        Guid? DefaultDeadLetterResyncStepId = null,
        string HelperCode = "");
    public async Task<bool> UpdateWebhookSecretAsync(Guid flowId, string webhookSecret)
    {
        var flow = await _context.IntegrationFlows
            .FirstOrDefaultAsync(existingFlow => existingFlow.Id == flowId);

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

        await using var transaction = await _context.Database.BeginTransactionAsync();

        var executions = _context.FlowExecutions.Where(e => e.FlowId == id);

        // Delete children before the flow. Subqueries keep this bounded by the flow id instead
        // of materialising thousands of execution ids into a SQLite IN () list.
        await _context.DeadLetterEntries
            .Where(entry => executions.Any(e => e.Id == entry.FlowExecutionId))
            .ExecuteDeleteAsync();
        await _context.StepPacketLogs
            .Where(packet => _context.StepExecutions.Any(step =>
                step.Id == packet.StepExecutionId && executions.Any(e => e.Id == step.FlowExecutionId)))
            .ExecuteDeleteAsync();
        await _context.StepExecutions
            .Where(step => executions.Any(e => e.Id == step.FlowExecutionId))
            .ExecuteDeleteAsync();
        await _context.FlowExecutions
            .Where(e => e.FlowId == id)
            .ExecuteDeleteAsync();

        // These tables have no database foreign key to the flow, so they need explicit cleanup.
        await _context.FlowTestRuns.Where(run => run.FlowId == id).ExecuteDeleteAsync();
        await _context.FlowTestCases.Where(test => test.FlowId == id).ExecuteDeleteAsync();
        await _context.FlowVersions.Where(version => version.FlowId == id).ExecuteDeleteAsync();

        _context.IntegrationFlows.Remove(flow);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task UpdatePersistedStateAsync(Guid flowId, string persistedStateJson)
    {
        var flow = await _context.IntegrationFlows
            .FirstOrDefaultAsync(existingFlow => existingFlow.Id == flowId);

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
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.Status == FlowStatus.Active);
    }

    public async Task<IEnumerable<IntegrationFlow>> GetActiveCronFlowsAsync()
    {
        return await _context.IntegrationFlows
            .AsNoTracking()
            .Where(f => f.Status == FlowStatus.Active && f.TriggerType == TriggerType.Cron && (f.CronExpression != "" || f.RunAt != null))
            .ToListAsync();
    }

    public async Task<IntegrationFlow?> SetScheduleStatusAsync(Guid flowId, FlowStatus status)
    {
        var flow = await _context.IntegrationFlows
            .FirstOrDefaultAsync(f => f.Id == flowId);

        if (flow == null) return null;

        flow.Status = status;
        // A paused flow must not be picked up by the scheduler. On resume, a cron schedule is
        // primed by the scheduler's next scan; a future one-time run is restored immediately.
        flow.NextRunAt = status == FlowStatus.Active
            ? flow.RunAt is DateTime runAt && runAt > DateTime.UtcNow ? runAt : null
            : null;
        flow.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return flow;
    }

    public async Task UpdateNextRunAtAsync(Guid flowId, DateTime? nextRunAt)
    {
        var flow = await _context.IntegrationFlows
            .FirstOrDefaultAsync(f => f.Id == flowId);

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
        target.HttpErrorMode = source.HttpErrorMode;
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

}
