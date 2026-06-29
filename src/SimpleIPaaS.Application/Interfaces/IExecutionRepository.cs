using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IExecutionRepository
{
    Task<FlowExecution?> GetFlowExecutionAsync(Guid id);
    Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync();

    Task AddFlowExecutionAsync(FlowExecution execution);
    Task UpdateFlowExecutionAsync(FlowExecution execution);
    
    Task AddStepExecutionAsync(StepExecution execution);
    Task UpdateStepExecutionAsync(StepExecution execution);
    Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId);
    
    Task AddDeadLetterEntryAsync(DeadLetterEntry entry);
    Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize);
    Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry);
}
