using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IExecutionRepository
{
    Task<FlowExecution?> GetFlowExecutionAsync(Guid id);
    Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(Guid? flowId = null, ExecutionStatus? status = null, int page = 1, int pageSize = 50);

    Task AddFlowExecutionAsync(FlowExecution execution);
    Task UpdateFlowExecutionAsync(FlowExecution execution);
    
    Task AddStepExecutionAsync(StepExecution execution);
    Task UpdateStepExecutionAsync(StepExecution execution);
    Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId);
    Task<StepExecution?> GetStepExecutionAsync(Guid flowExecutionId, Guid stepId);
    Task AddStepPacketLogAsync(StepPacketLog packet);
    Task<IEnumerable<StepPacketLog>> GetStepPacketLogsAsync(Guid stepExecutionId);

    Task AddDeadLetterEntryAsync(DeadLetterEntry entry);
    Task<DeadLetterEntry?> GetDeadLetterAsync(Guid id);
    Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize);
    Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAcrossTenantsAsync(int batchSize);
    Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null);
    Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId);
    Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry);
}
