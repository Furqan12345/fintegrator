using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Persistence;

public sealed class FlowTestRepository : IFlowTestRepository
{
    private readonly IPaaSContext _context;
    private readonly ITenantContext _tenantContext;

    public FlowTestRepository(IPaaSContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<IReadOnlyList<FlowTestCase>> GetByFlowIdAsync(Guid flowId) =>
        await _context.FlowTestCases.AsNoTracking().Where(test => test.FlowId == flowId).OrderBy(test => test.Name).ToListAsync();

    public Task<FlowTestCase?> GetAsync(Guid id) => _context.FlowTestCases.FirstOrDefaultAsync(test => test.Id == id);

    public async Task AddAsync(FlowTestCase testCase)
    {
        if (testCase.TenantId == Guid.Empty) testCase.TenantId = _tenantContext.TenantId;
        _context.FlowTestCases.Add(testCase);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> UpdateAsync(FlowTestCase testCase)
    {
        var existing = await _context.FlowTestCases.FirstOrDefaultAsync(test => test.Id == testCase.Id);
        if (existing == null) return false;
        existing.Name = testCase.Name;
        existing.Description = testCase.Description;
        existing.Enabled = testCase.Enabled;
        existing.RequiredForPublish = testCase.RequiredForPublish;
        existing.DefinitionJson = testCase.DefinitionJson;
        existing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var test = await _context.FlowTestCases.FirstOrDefaultAsync(candidate => candidate.Id == id);
        if (test == null) return false;
        _context.FlowTestCases.Remove(test);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task AddRunAsync(FlowTestRun run)
    {
        if (run.TenantId == Guid.Empty) run.TenantId = _tenantContext.TenantId;
        _context.FlowTestRuns.Add(run);
        await _context.SaveChangesAsync();
    }

    public Task<FlowTestRun?> GetRunAsync(Guid id) => _context.FlowTestRuns.FirstOrDefaultAsync(run => run.Id == id);

    public async Task<IReadOnlyList<FlowTestRun>> GetRunsAsync(Guid testCaseId) =>
        await _context.FlowTestRuns.AsNoTracking().Where(run => run.TestCaseId == testCaseId)
            .OrderByDescending(run => run.StartedAt).Take(50).ToListAsync();

    public Task<FlowTestRun?> GetLatestRunAsync(Guid testCaseId) =>
        _context.FlowTestRuns.Where(run => run.TestCaseId == testCaseId).OrderByDescending(run => run.StartedAt).FirstOrDefaultAsync();

    public async Task UpdateRunAsync(FlowTestRun run)
    {
        _context.FlowTestRuns.Update(run);
        await _context.SaveChangesAsync();
    }
}
