using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class FlowExecutionDto
{
    public Guid Id { get; set; }
    public Guid FlowId { get; set; }
    public Guid TenantId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
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
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int HttpStatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
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
