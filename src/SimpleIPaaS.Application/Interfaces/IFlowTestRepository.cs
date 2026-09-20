using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IFlowTestRepository
{
    Task<IReadOnlyList<FlowTestCase>> GetByFlowIdAsync(Guid flowId);
    Task<FlowTestCase?> GetAsync(Guid id);
    Task AddAsync(FlowTestCase testCase);
    Task<bool> UpdateAsync(FlowTestCase testCase);
    Task<bool> DeleteAsync(Guid id);
    Task AddRunAsync(FlowTestRun run);
    Task<FlowTestRun?> GetRunAsync(Guid id);
    Task<IReadOnlyList<FlowTestRun>> GetRunsAsync(Guid testCaseId);
    Task<FlowTestRun?> GetLatestRunAsync(Guid testCaseId);
    Task UpdateRunAsync(FlowTestRun run);
}
