using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Packet logs in the relational database. This is the code that used to live in
// ExecutionRepository; it moved here so the same six operations can also be served by
// Elasticsearch without the callers (or the UI) knowing which one answered.
public sealed class SqlPacketLogStore : IPacketLogStore
{
    private readonly IPaaSContext _context;

    public SqlPacketLogStore(IPaaSContext context)
    {
        _context = context;
    }

    public async Task AddAsync(StepPacketLog packet, CancellationToken cancellationToken = default)
    {
        _context.StepPacketLogs.Add(packet);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StepPacketLog>> GetByStepExecutionAsync(Guid stepExecutionId, CancellationToken cancellationToken = default)
    {
        return await _context.StepPacketLogs
            .Where(packet => packet.StepExecutionId == stepExecutionId)
            .OrderBy(packet => packet.Sequence)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StepPacketLogSummary>> GetSummariesAsync(Guid flowExecutionId, CancellationToken cancellationToken = default)
    {
        return await (from packet in _context.StepPacketLogs.AsNoTracking()
                      join step in _context.StepExecutions.AsNoTracking() on packet.StepExecutionId equals step.Id
                      where step.FlowExecutionId == flowExecutionId
                      orderby packet.StepExecutionId, packet.Sequence
                      select new StepPacketLogSummary(
                          packet.Id,
                          packet.StepExecutionId,
                          packet.Sequence,
                          packet.Kind,
                          packet.PageNumber,
                          packet.Attempt,
                          packet.HttpMethod,
                          packet.RequestUrl,
                          packet.StatusCode,
                          packet.StartedAt,
                          packet.CompletedAt,
                          packet.DurationMs,
                          packet.Error,
                          packet.RequestHeadersJson.Length,
                          packet.RequestBody.Length,
                          packet.ResponseHeadersJson.Length,
                          packet.ResponseBody.Length))
            .ToListAsync(cancellationToken);
    }

    public async Task<StepPacketBody?> GetBodyAsync(Guid flowExecutionId, Guid packetId, CancellationToken cancellationToken = default)
    {
        return await (from packet in _context.StepPacketLogs.AsNoTracking()
                      join step in _context.StepExecutions.AsNoTracking() on packet.StepExecutionId equals step.Id
                      where packet.Id == packetId && step.FlowExecutionId == flowExecutionId
                      select new StepPacketBody(
                          packet.Id,
                          packet.RequestHeadersJson,
                          packet.RequestBody,
                          packet.ResponseHeadersJson,
                          packet.ResponseBody))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PacketLogListItem>> GetPageAsync(PacketLogPageFilter filter, CancellationToken cancellationToken = default)
    {
        var query =
            from packet in _context.StepPacketLogs.AsNoTracking()
            join step in _context.StepExecutions.AsNoTracking() on packet.StepExecutionId equals step.Id
            join run in _context.FlowExecutions.AsNoTracking() on step.FlowExecutionId equals run.Id
            join node in _context.IntegrationSteps.AsNoTracking() on step.StepId equals node.Id into nodes
            from node in nodes.DefaultIfEmpty()
            where packet.Kind == "HTTP"
                && (filter.FlowId == null || run.FlowId == filter.FlowId)
                && (filter.FlowExecutionId == null || run.Id == filter.FlowExecutionId)
                && (filter.IntegrationId == null || run.IntegrationId == filter.IntegrationId)
                && (filter.ConnectionId == null || (node != null && node.ConnectionId == filter.ConnectionId))
                && (!filter.ManualOnly || node == null || node.ConnectionId == null)
            select new { packet, step, run };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x =>
                x.packet.RequestUrl.Contains(search) ||
                x.run.FlowName.Contains(search) ||
                x.run.IntegrationName.Contains(search) ||
                x.step.NodeName.Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(filter.Method))
        {
            var method = filter.Method;
            query = query.Where(x => x.packet.HttpMethod == method);
        }
        if (string.Equals(filter.Outcome, "success", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.packet.StatusCode != null && x.packet.StatusCode >= 200 && x.packet.StatusCode < 300);
        }
        else if (string.Equals(filter.Outcome, "failure", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.packet.StatusCode == null || x.packet.StatusCode < 200 || x.packet.StatusCode >= 300);
        }
        if (filter.Hours.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-filter.Hours.Value);
            query = query.Where(x => x.packet.StartedAt >= since);
        }

        var rows = await query
            .OrderByDescending(x => x.packet.StartedAt)
            .Skip((Math.Max(1, filter.Page) - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        var stepIds = rows.Select(x => x.step.StepId).Distinct().ToList();
        var connectionNames = await _context.IntegrationSteps.AsNoTracking()
            .Where(node => stepIds.Contains(node.Id) && node.ConnectionId != null)
            .Join(_context.Connections.AsNoTracking(), node => node.ConnectionId, connection => connection.Id,
                (node, connection) => new { node.Id, connection.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        return rows.Select(x => new PacketLogListItem(
            x.packet.Id,
            x.run.Id,
            x.packet.StartedAt,
            x.run.FlowName,
            x.run.IntegrationName,
            x.step.NodeName,
            connectionNames.TryGetValue(x.step.StepId, out var connectionName) ? connectionName : null,
            x.packet.HttpMethod,
            x.packet.RequestUrl,
            x.packet.StatusCode,
            x.packet.DurationMs,
            x.packet.PageNumber,
            x.packet.Attempt,
            x.packet.Error)).ToList();
    }

    public async Task<PacketLogDetailItem?> GetDetailAsync(Guid packetId, CancellationToken cancellationToken = default)
    {
        var row = await (
            from packet in _context.StepPacketLogs.AsNoTracking()
            join step in _context.StepExecutions.AsNoTracking() on packet.StepExecutionId equals step.Id
            join run in _context.FlowExecutions.AsNoTracking() on step.FlowExecutionId equals run.Id
            where packet.Id == packetId
            select new { packet, step, run })
            .FirstOrDefaultAsync(cancellationToken);
        if (row == null)
        {
            return null;
        }

        var connectionName = await (
            from node in _context.IntegrationSteps.AsNoTracking()
            join connection in _context.Connections.AsNoTracking() on node.ConnectionId equals (Guid?)connection.Id
            where node.Id == row.step.StepId
            select connection.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return new PacketLogDetailItem(
            row.packet.Id,
            row.run.Id,
            row.packet.StartedAt,
            row.packet.CompletedAt,
            row.packet.DurationMs,
            row.run.FlowName,
            row.run.IntegrationName,
            row.step.NodeName,
            connectionName,
            row.packet.HttpMethod,
            row.packet.RequestUrl,
            row.packet.StatusCode,
            row.packet.PageNumber,
            row.packet.Attempt,
            row.packet.Error,
            row.packet.RequestHeadersJson,
            row.packet.RequestBody,
            row.packet.ResponseHeadersJson,
            row.packet.ResponseBody);
    }
}
