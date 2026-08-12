using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

public enum DeadLetterReplayOutcome
{
    NotFound,
    Skipped,
    PostReplayNotAllowed,
    Replayed
}

public class DeadLetterReplayResult
{
    public DeadLetterReplayOutcome Outcome { get; init; }
    public DeadLetterEntry? Entry { get; init; }
}

public class DeadLetterAttempt
{
    public int Attempt { get; set; }
    public DateTime At { get; set; }
    public int? StatusCode { get; set; }
    public string Error { get; set; } = string.Empty;
}

public class DeadLetterService
{
    private static readonly string[] ReplaySafeMethods = { "GET", "PUT", "DELETE" };
    private static readonly string[] OutstandingStatuses = { "Pending", "Retrying" };

    private static readonly JsonSerializerOptions AttemptHistoryOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

    public async Task<DeadLetterReplayResult> ReplayEntryAsync(Guid entryId, bool force, CancellationToken cancellationToken = default)
    {
        var entry = await _executionRepository.GetDeadLetterAsync(entryId);
        if (entry == null)
        {
            return new DeadLetterReplayResult { Outcome = DeadLetterReplayOutcome.NotFound };
        }

        if (entry.Status != "Pending" && entry.Status != "Retrying")
        {
            return Skipped(entry);
        }

        var flowExecution = await _executionRepository.GetFlowExecutionAsync(entry.FlowExecutionId);
        if (flowExecution == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "FlowExecution not found";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return Skipped(entry);
        }

        var flow = await _integrationRepository.GetByIdAsync(flowExecution.FlowId);
        if (flow == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "IntegrationFlow not found";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return Skipped(entry);
        }

        var step = flow.Nodes.FirstOrDefault(n => n.Id == entry.StepId);
        if (step == null)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "Step not found in flow";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return Skipped(entry);
        }

        if (step.StepType != StepType.HttpAction)
        {
            entry.Status = "Discarded";
            entry.ErrorMessage = "Step type is not replayable";
            await _executionRepository.UpdateDeadLetterEntryAsync(entry);
            return Skipped(entry);
        }

        var replaySafe = ReplaySafeMethods.Contains(step.HttpMethod, StringComparer.OrdinalIgnoreCase);
        var postOptIn = string.Equals(step.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && flow.AllowPostReplay;

        if (!replaySafe && !postOptIn)
        {
            return new DeadLetterReplayResult
            {
                Outcome = force ? DeadLetterReplayOutcome.PostReplayNotAllowed : DeadLetterReplayOutcome.Skipped,
                Entry = entry
            };
        }

        var replayFlowStateJson = string.IsNullOrWhiteSpace(entry.FlowStateJson) ? "{}" : entry.FlowStateJson;
        var resolved = false;

        try
        {
            _logger.LogInformation("Replaying dead letter {DeadLetterId} for execution {ExecutionId} (step {StepId})",
                entry.Id, entry.FlowExecutionId, entry.StepId);

            var replay = await _flowExecutor.ExecuteHttpNodeAsync(
                step, replayFlowStateJson, flow.PersistedStateJson, cancellationToken);
            var statusCode = replay.StatusCode;

            if (statusCode >= 200 && statusCode < 300)
            {
                var resolvedAt = DateTime.UtcNow;
                entry.Status = "Resolved";
                entry.LastRetriedAt = resolvedAt;
                entry.ResolvedAt = resolvedAt;
                AppendAttempt(entry, statusCode, string.Empty, resolvedAt);
                resolved = true;
                _logger.LogInformation("Dead letter {DeadLetterId} resolved with status {StatusCode}", entry.Id, statusCode);
            }
            else
            {
                entry.RetryCount++;
                entry.LastRetriedAt = DateTime.UtcNow;
                entry.ErrorMessage = $"Failed with status code {statusCode}: {replay.Response}";
                AppendAttempt(entry, statusCode, entry.ErrorMessage, entry.LastRetriedAt.Value);

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
            AppendAttempt(entry, null, ex.Message, entry.LastRetriedAt.Value);
            if (entry.RetryCount >= 5)
            {
                entry.Status = "Discarded";
            }

            _logger.LogError(ex, "Dead letter {DeadLetterId} replay threw (attempt {RetryCount})", entry.Id, entry.RetryCount);
        }

        await _executionRepository.UpdateDeadLetterEntryAsync(entry);

        if (resolved)
        {
            await ApplyRecoveryAsync(entry, flowExecution);
        }

        return new DeadLetterReplayResult { Outcome = DeadLetterReplayOutcome.Replayed, Entry = entry };
    }

    private async Task ApplyRecoveryAsync(DeadLetterEntry entry, FlowExecution flowExecution)
    {
        var recoveredAt = entry.ResolvedAt ?? DateTime.UtcNow;

        var stepExecution = await _executionRepository.GetStepExecutionAsync(entry.FlowExecutionId, entry.StepId);
        if (stepExecution != null)
        {
            stepExecution.Status = ExecutionStatus.Recovered;
            stepExecution.RecoveredAt = recoveredAt;
            stepExecution.RecoveredByDeadLetterId = entry.Id;
            await _executionRepository.UpdateStepExecutionAsync(stepExecution);
        }

        var entries = (await _executionRepository.GetDeadLettersByExecutionAsync(entry.FlowExecutionId)).ToList();
        if (entries.All(d => d.Id != entry.Id))
        {
            entries.Add(entry);
        }

        if (entries.Any(d => d.Id != entry.Id && OutstandingStatuses.Contains(d.Status, StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        if (flowExecution.Status == ExecutionStatus.InProgress || flowExecution.Status == ExecutionStatus.Queued)
        {
            return;
        }

        var resolvedCount = entries.Count(d => string.Equals(d.Status, "Resolved", StringComparison.OrdinalIgnoreCase));
        var moved = Math.Min(flowExecution.FailedRecords, resolvedCount);

        flowExecution.FailedRecords -= moved;
        flowExecution.SuccessRecords += moved;
        flowExecution.Status = ExecutionStatus.Recovered;
        flowExecution.RecoveredAt = recoveredAt;
        flowExecution.CompletedAt ??= recoveredAt;

        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);

        _logger.LogInformation("Execution {ExecutionId} recovered after all dead letters were resolved", flowExecution.Id);
    }

    private static void AppendAttempt(DeadLetterEntry entry, int? statusCode, string error, DateTime at)
    {
        var history = ParseAttemptHistory(entry.AttemptHistoryJson);
        history.Add(new DeadLetterAttempt
        {
            Attempt = history.Count + 1,
            At = at,
            StatusCode = statusCode,
            Error = error
        });

        entry.AttemptHistoryJson = JsonSerializer.Serialize(history, AttemptHistoryOptions);
    }

    private static List<DeadLetterAttempt> ParseAttemptHistory(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<DeadLetterAttempt>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<DeadLetterAttempt>>(json, AttemptHistoryOptions) ?? new List<DeadLetterAttempt>();
        }
        catch (JsonException)
        {
            return new List<DeadLetterAttempt>();
        }
    }

    private static DeadLetterReplayResult Skipped(DeadLetterEntry entry) =>
        new() { Outcome = DeadLetterReplayOutcome.Skipped, Entry = entry };

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
