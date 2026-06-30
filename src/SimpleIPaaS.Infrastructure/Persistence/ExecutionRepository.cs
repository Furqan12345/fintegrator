using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
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

    public async Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync()
    {
        return await _context.FlowExecutions
            .OrderByDescending(e => e.StartedAt)
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

    public async Task AddDeadLetterEntryAsync(DeadLetterEntry entry)
    {
        if (entry.TenantId == Guid.Empty)
        {
            entry.TenantId = _tenantContext.TenantId;
        }

        _context.DeadLetterEntries.Add(entry);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize)
    {
        return await _context.DeadLetterEntries
            .Where(d => d.Status == "Pending")
            .OrderBy(d => d.CreatedAt)
            .Take(batchSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync()
    {
        return await _context.DeadLetterEntries
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry)
    {
        _context.DeadLetterEntries.Update(entry);
        await _context.SaveChangesAsync();
    }
}
