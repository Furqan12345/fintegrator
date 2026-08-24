using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public record StepExecutionSummary(
    Guid Id,
    Guid FlowExecutionId,
    Guid StepId,
    string NodeName,
    ExecutionStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    DateTime? RecoveredAt,
    Guid? RecoveredByDeadLetterId,
    int HttpStatusCode,
    string? ErrorMessage,
    int ReceivedInputSize,
    int RequestPayloadSize,
    int ResponsePayloadSize);

public record StepPacketLogSummary(
    Guid Id,
    Guid StepExecutionId,
    int Sequence,
    string Kind,
    int? PageNumber,
    int? Attempt,
    string HttpMethod,
    string RequestUrl,
    int? StatusCode,
    DateTime StartedAt,
    DateTime? CompletedAt,
    long? DurationMs,
    string? Error,
    int RequestHeadersSize,
    int RequestBodySize,
    int ResponseHeadersSize,
    int ResponseBodySize);

public record StepPayload(Guid Id, string ReceivedInput, string RequestPayload, string ResponsePayload);

public record StepPacketBody(Guid Id, string RequestHeadersJson, string RequestBody, string ResponseHeadersJson, string ResponseBody);

public interface IExecutionRepository
{
    Task<FlowExecution?> GetFlowExecutionAsync(Guid id);
    Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(Guid? flowId = null, ExecutionStatus? status = null, int page = 1, int pageSize = 50, Guid? integrationId = null);

    Task AddFlowExecutionAsync(FlowExecution execution);
    Task UpdateFlowExecutionAsync(FlowExecution execution);
    
    Task AddStepExecutionAsync(StepExecution execution);
    Task UpdateStepExecutionAsync(StepExecution execution);
    Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId);
    Task<StepExecution?> GetStepExecutionAsync(Guid flowExecutionId, Guid stepId);
    Task<IReadOnlyList<StepExecutionSummary>> GetStepExecutionSummariesAsync(Guid flowExecutionId);
    Task<StepPayload?> GetStepPayloadAsync(Guid flowExecutionId, Guid stepExecutionId);
    Task AddStepPacketLogAsync(StepPacketLog packet);
    Task<IEnumerable<StepPacketLog>> GetStepPacketLogsAsync(Guid stepExecutionId);
    Task<IReadOnlyList<StepPacketLogSummary>> GetStepPacketLogSummariesAsync(Guid flowExecutionId);
    Task<StepPacketBody?> GetStepPacketBodyAsync(Guid flowExecutionId, Guid packetId);

    Task AddDeadLetterEntryAsync(DeadLetterEntry entry);
    Task<DeadLetterEntry?> GetDeadLetterAsync(Guid id);
    Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize);
    Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAcrossTenantsAsync(int batchSize);
    Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null);
    Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId);
    Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry);
}
