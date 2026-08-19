using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class FlowExecutionDto
{
    public Guid Id { get; set; }
    public Guid FlowId { get; set; }
    public Guid TenantId { get; set; }
    public string FlowName { get; set; } = string.Empty;
    public Guid? IntegrationId { get; set; }
    public string IntegrationName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? RecoveredAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string TriggerSource { get; set; } = string.Empty;
    public int TotalRecords { get; set; }
    public int SuccessRecords { get; set; }
    public int FailedRecords { get; set; }
}

public class StepExecutionDto
{
    public Guid Id { get; set; }
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? RecoveredAt { get; set; }
    public Guid? RecoveredByDeadLetterId { get; set; }
    public int HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int ReceivedInputSize { get; set; }
    public int RequestPayloadSize { get; set; }
    public int ResponsePayloadSize { get; set; }
    public List<StepPacketLogDto> Packets { get; set; } = new();
}

public class StepPacketLogDto
{
    public Guid Id { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public int? Attempt { get; set; }
    public string HttpMethod { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public int RequestHeadersSize { get; set; }
    public int RequestBodySize { get; set; }
    public int ResponseHeadersSize { get; set; }
    public int ResponseBodySize { get; set; }
}

public class StepPayloadDto
{
    public Guid Id { get; set; }
    public string ReceivedInput { get; set; } = string.Empty;
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
}

public class StepPacketBodyDto
{
    public Guid Id { get; set; }
    public string RequestHeadersJson { get; set; } = "{}";
    public string RequestBody { get; set; } = string.Empty;
    public string ResponseHeadersJson { get; set; } = "{}";
    public string ResponseBody { get; set; } = string.Empty;
}

public class DeadLetterEntryDto
{
    public Guid Id { get; set; }
    public Guid FlowExecutionId { get; set; }
    public Guid StepId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastRetriedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}
