using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public class DeadLetterService
{
    private static readonly string[] ReplaySafeMethods = { "GET", "PUT", "DELETE" };

    private readonly IExecutionRepository _executionRepository;
    private readonly IIntegrationRepository _integrationRepository;
    private readonly FlowExecutor _flowExecutor;
    private readonly ILogger<DeadLetterService> _logger;

    public DeadLetterService(
        IExecutionRepository executionRepository,
        IIntegrationRepository integrationRepository,
        FlowExecutor flowExecutor,
        ILogger<DeadLetterService> logger)
    {
        _executionRepository = executionRepository;
        _integrationRepository = integrationRepository;
        _flowExecutor = flowExecutor;
        _logger = logger;
    }

    public async Task<DeadLetterEntry?> ReplayEntryAsync(Guid entryId, bool force, CancellationToken cancellationToken = default)
    {
        var entry = await _executionRepository.GetDeadLetterAsync(entryId);
        if (entry == null)
        {
            return null;
        }

        if (entry.Status != "Pending" && entry.Status != "Retrying")
        {
            return entry;
        }

        var flowExecution = await _executionRepository.GetFlowExecutionAsync(entry.FlowExecutionId);
        if (flowExecution == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "FlowExecution not found";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return entry;
        }

        var flow = await _integrationRepository.GetByIdAsync(flowExecution.FlowId);
        if (flow == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "IntegrationFlow not found";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return entry;
        }

        var step = flow.Nodes.FirstOrDefault(n => n.Id == entry.StepId);
        if (step == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "Step not found in flow";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return entry;
        }

        if (step.StepType != StepType.HttpAction)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "Step type is not replayable";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return entry;
        }

        var replaySafe = ReplaySafeMethods.Contains(step.HttpMethod, StringComparer.OrdinalIgnoreCase);
        if (!replaySafe && !force)
        {
            return entry;
        }

        try
        {
            _logger.LogInformation("Replaying dead letter {DeadLetterId} for execution {ExecutionId} (step {StepId})",
                entry.Id, entry.FlowExecutionId, entry.StepId);

            var (statusCode, response, _) = await _flowExecutor.ExecuteHttpNodeAsync(
                step, "{}", flow.PersistedStateJson, cancellationToken);

            if (statusCode >= 200 && statusCode < 300)
            {
                entry.Status = "Resolved";
                entry.LastRetriedAt = DateTime.UtcNow;
                entry.ErrorMessage = string.Empty;
                _logger.LogInformation("Dead letter {DeadLetterId} resolved with status {StatusCode}", entry.Id, statusCode);
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

                _logger.LogWarning("Dead letter {DeadLetterId} replay failed with status {StatusCode} (attempt {RetryCount})",
                    entry.Id, statusCode, entry.RetryCount);
            }
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

            _logger.LogError(ex, "Dead letter {DeadLetterId} replay threw (attempt {RetryCount})", entry.Id, entry.RetryCount);
        }

        await _executionRepository.UpdateDeadLetterEntryAsync(entry);
        return entry;
    }

    public async Task<DeadLetterEntry?> DiscardEntryAsync(Guid entryId)
    {
        var entry = await _executionRepository.GetDeadLetterAsync(entryId);
        if (entry == null)
        {
            return null;
        }

        entry.Status = "Discarded";
        await _executionRepository.UpdateDeadLetterEntryAsync(entry);
        return entry;
    }
}
