using System;
using System.Linq;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public class DeadLetterService
{
    private readonly IExecutionRepository _executionRepository;
    private readonly ITransportEngine _transportEngine;
    private readonly IIntegrationRepository _integrationRepository;

    public DeadLetterService(
        IExecutionRepository executionRepository,
        ITransportEngine transportEngine,
        IIntegrationRepository integrationRepository)
    {
        _executionRepository = executionRepository;
        _transportEngine = transportEngine;
        _integrationRepository = integrationRepository;
    }

    public async Task ProcessPendingLettersAsync(int batchSize = 100)
    {
        var pending = await _executionRepository.GetPendingDeadLettersAsync(batchSize);

        foreach (var entry in pending)
        {
            try
            {
                var flowExecution = await _executionRepository.GetFlowExecutionAsync(entry.FlowExecutionId);
                if (flowExecution == null)
                {
                    entry.Status = "Discarded";
                    entry.ErrorMessage = "FlowExecution not found";
                    await _executionRepository.UpdateDeadLetterEntryAsync(entry);
                    continue;
                }

                var flow = await _integrationRepository.GetByIdAsync(flowExecution.FlowId);
                if (flow == null)
                {
                    entry.Status = "Discarded";
                    entry.ErrorMessage = "IntegrationFlow not found";
                    await _executionRepository.UpdateDeadLetterEntryAsync(entry);
                    continue;
                }

                var step = flow.Nodes.FirstOrDefault(n => n.Id == entry.StepId);
                if (step == null)
                {
                    entry.Status = "Discarded";
                    entry.ErrorMessage = "Step not found in flow";
                    await _executionRepository.UpdateDeadLetterEntryAsync(entry);
                    continue;
                }

                var (statusCode, response) = await _transportEngine.DispatchAsync(step, entry.Payload, step.ConnectionId);

                if (statusCode >= 200 && statusCode < 300)
                {
                    entry.Status = "Retried";
                    entry.LastRetriedAt = DateTime.UtcNow;
                    entry.ErrorMessage = string.Empty;
                }
                else
                {
                    entry.RetryCount++;
                    entry.LastRetriedAt = DateTime.UtcNow;
                    entry.ErrorMessage = $"Failed with status code {statusCode}: {response}";
                    
                    if (entry.RetryCount >= 5)
                    {
                        entry.Status = "Discarded";
                    }
                }

                await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            }
            catch (Exception ex)
            {
                entry.RetryCount++;
                entry.LastRetriedAt = DateTime.UtcNow;
                entry.ErrorMessage = ex.Message;
                if (entry.RetryCount >= 5)
                {
                    entry.Status = "Discarded";
                }
                await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            }
        }
    }
}
