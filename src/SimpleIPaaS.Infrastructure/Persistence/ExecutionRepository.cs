using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class ExecutionRepository : IExecutionRepository
{
    private readonly IPaaSContext _context;
    private readonly ITenantContext _tenantContext;

    public ExecutionRepository(IPaaSContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<FlowExecution?> GetFlowExecutionAsync(Guid id)
    {
        return await _context.FlowExecutions.FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(Guid? flowId = null, ExecutionStatus? status = null, int page = 1, int pageSize = 50)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 50 : Math.Min(pageSize, 200);

        var query = _context.FlowExecutions.AsQueryable();

        if (flowId.HasValue && flowId.Value != Guid.Empty)
        {
            query = query.Where(e => e.FlowId == flowId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(e => e.Status == status.Value);
        }

        return await query
            .OrderByDescending(e => e.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task AddFlowExecutionAsync(FlowExecution execution)
    {
        if (execution.TenantId == Guid.Empty)
        {
            execution.TenantId = _tenantContext.TenantId;
        }

        _context.FlowExecutions.Add(execution);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateFlowExecutionAsync(FlowExecution execution)
    {
        _context.FlowExecutions.Update(execution);
        await _context.SaveChangesAsync();
    }

    public async Task AddStepExecutionAsync(StepExecution execution)
    {
        if (execution.TenantId == Guid.Empty)
        {
            execution.TenantId = _tenantContext.TenantId;
        }

        _context.StepExecutions.Add(execution);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateStepExecutionAsync(StepExecution execution)
    {
        _context.StepExecutions.Update(execution);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId)
    {
        return await _context.StepExecutions
            .Where(s => s.FlowExecutionId == flowExecutionId)
            .OrderBy(s => s.StartedAt)
            .ToListAsync();
    }

    public async Task<StepExecution?> GetStepExecutionAsync(Guid flowExecutionId, Guid stepId)
    {
        return await _context.StepExecutions
            .Where(s => s.FlowExecutionId == flowExecutionId && s.StepId == stepId)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<StepExecutionSummary>> GetStepExecutionSummariesAsync(Guid flowExecutionId)
    {
        return await _context.StepExecutions
            .AsNoTracking()
            .Where(s => s.FlowExecutionId == flowExecutionId)
            .OrderBy(s => s.StartedAt)
            .Select(s => new StepExecutionSummary(
                s.Id,
                s.FlowExecutionId,
                s.StepId,
                s.NodeName,
                s.Status,
                s.StartedAt,
                s.CompletedAt,
                s.RecoveredAt,
                s.RecoveredByDeadLetterId,
                s.HttpStatusCode,
                s.ErrorMessage,
                s.ReceivedInput.Length,
                s.RequestPayload.Length,
                s.ResponsePayload.Length))
            .ToListAsync();
    }

    public async Task<StepPayload?> GetStepPayloadAsync(Guid flowExecutionId, Guid stepExecutionId)
    {
        return await _context.StepExecutions
            .AsNoTracking()
            .Where(s => s.FlowExecutionId == flowExecutionId && s.Id == stepExecutionId)
            .Select(s => new StepPayload(s.Id, s.ReceivedInput, s.RequestPayload, s.ResponsePayload))
            .FirstOrDefaultAsync();
    }

    public async Task AddStepPacketLogAsync(StepPacketLog packet)
    {
        if (packet.TenantId == Guid.Empty)
        {
            packet.TenantId = _tenantContext.TenantId;
        }

        _context.StepPacketLogs.Add(packet);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<StepPacketLog>> GetStepPacketLogsAsync(Guid stepExecutionId)
    {
        return await _context.StepPacketLogs
            .Where(packet => packet.StepExecutionId == stepExecutionId)
            .OrderBy(packet => packet.Sequence)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<StepPacketLogSummary>> GetStepPacketLogSummariesAsync(Guid flowExecutionId)
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
            .ToListAsync();
    }

    public async Task<StepPacketBody?> GetStepPacketBodyAsync(Guid flowExecutionId, Guid packetId)
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
            .FirstOrDefaultAsync();
    }

    public async Task AddDeadLetterEntryAsync(DeadLetterEntry entry)
    {
        if (entry.TenantId == Guid.Empty)
        {
            entry.TenantId = _tenantContext.TenantId;
        }

        _context.DeadLetterEntries.Add(entry);
        await _context.SaveChangesAsync();
    }

    public async Task<DeadLetterEntry?> GetDeadLetterAsync(Guid id)
    {
        return await _context.DeadLetterEntries.FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize)
    {
        return await _context.DeadLetterEntries
            .Where(d => d.Status == "Pending")
            .OrderBy(d => d.CreatedAt)
            .Take(batchSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAcrossTenantsAsync(int batchSize)
    {
        return await _context.DeadLetterEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.Status == "Pending")
            .OrderBy(d => d.CreatedAt)
            .Take(batchSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null)
    {
        var query = _context.DeadLetterEntries.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        return await query
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId)
    {
        return await _context.DeadLetterEntries
            .Where(d => d.FlowExecutionId == flowExecutionId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry)
    {
        _context.DeadLetterEntries.Update(entry);
        await _context.SaveChangesAsync();
    }
}
