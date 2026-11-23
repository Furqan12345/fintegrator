using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Persistence;

public sealed class FlowTestRepository : IFlowTestRepository
{
    private readonly IPaaSContext _context;

    public FlowTestRepository(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<FlowTestCase>> GetByFlowIdAsync(Guid flowId) =>
        await _context.FlowTestCases.AsNoTracking().Where(test => test.FlowId == flowId).OrderBy(test => test.Name).ToListAsync();

    public Task<FlowTestCase?> GetAsync(Guid id) => _context.FlowTestCases.FirstOrDefaultAsync(test => test.Id == id);

    public async Task AddAsync(FlowTestCase testCase)
    {
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

        var runs = await _context.FlowTestRuns.Where(run => run.TestCaseId == id).ToListAsync();
        await DeleteRunArtifactsAsync(runs);
        _context.FlowTestRuns.RemoveRange(runs);
        _context.FlowTestCases.Remove(test);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task AddRunAsync(FlowTestRun run)
    {
        _context.FlowTestRuns.Add(run);
        await _context.SaveChangesAsync();
    }

    public Task<FlowTestRun?> GetRunAsync(Guid id) => _context.FlowTestRuns.FirstOrDefaultAsync(run => run.Id == id);

    public async Task<IReadOnlyList<FlowTestRun>> GetRunsAsync(Guid testCaseId) =>
        await _context.FlowTestRuns.AsNoTracking().Where(run => run.TestCaseId == testCaseId)
            .OrderByDescending(run => run.StartedAt).Take(20).ToListAsync();

    public async Task<IReadOnlyList<FlowTestRun>> GetRunsBySuiteAsync(Guid suiteRunId) =>
        await _context.FlowTestRuns.AsNoTracking().Where(run => run.SuiteRunId == suiteRunId)
            .OrderBy(run => run.StartedAt).ToListAsync();

    public Task<FlowTestRun?> GetLatestRunAsync(Guid testCaseId) =>
        _context.FlowTestRuns.Where(run => run.TestCaseId == testCaseId).OrderByDescending(run => run.StartedAt).FirstOrDefaultAsync();

    public async Task<IReadOnlyList<FlowTestRun>> GetInterruptedRunsAsync() =>
        await _context.FlowTestRuns.Where(run => run.Status == "Queued" || run.Status == "Running").ToListAsync();

    public async Task UpdateRunAsync(FlowTestRun run)
    {
        _context.FlowTestRuns.Update(run);
        await _context.SaveChangesAsync();
    }

    public async Task RecordRunTimestampsAsync(Guid testCaseId, DateTime at, bool passed)
    {
        await _context.FlowTestCases.Where(test => test.Id == testCaseId).ExecuteUpdateAsync(setters =>
        {
            setters.SetProperty(test => test.LastRunAt, at);
            if (passed) setters.SetProperty(test => test.LastPassedAt, at);
        });
    }

    public async Task TrimRunsAsync(Guid testCaseId, int keep = 20)
    {
        var stale = await _context.FlowTestRuns.Where(run => run.TestCaseId == testCaseId)
            .OrderByDescending(run => run.StartedAt).Skip(keep).ToListAsync();
        if (stale.Count == 0) return;

        await DeleteRunArtifactsAsync(stale);
        _context.FlowTestRuns.RemoveRange(stale);
        await _context.SaveChangesAsync();
    }

    private async Task DeleteRunArtifactsAsync(IReadOnlyCollection<FlowTestRun> runs)
    {
        var executionIds = runs.Select(run => run.FlowExecutionId).Where(id => id != Guid.Empty).Distinct().ToList();
        if (executionIds.Count == 0) return;

        var testExecutionIds = await _context.FlowExecutions
            .Where(execution => executionIds.Contains(execution.Id) && execution.IsTest)
            .Select(execution => execution.Id)
            .ToListAsync();
        if (testExecutionIds.Count == 0) return;

        var stepIds = await _context.StepExecutions
            .Where(step => testExecutionIds.Contains(step.FlowExecutionId))
            .Select(step => step.Id)
            .ToListAsync();
        if (stepIds.Count > 0)
        {
            await ColdStorage.DeleteForStepsAsync(_context, _context.StepExecutions.Where(step => stepIds.Contains(step.Id)).Select(step => step.Id));
            await _context.StepPacketLogs.Where(packet => stepIds.Contains(packet.StepExecutionId))
                .ExecuteDeleteAsync();
            await _context.StepExecutions.Where(step => stepIds.Contains(step.Id)).ExecuteDeleteAsync();
        }

        await _context.FlowExecutions.Where(execution => testExecutionIds.Contains(execution.Id))
            .ExecuteDeleteAsync();
    }
}
