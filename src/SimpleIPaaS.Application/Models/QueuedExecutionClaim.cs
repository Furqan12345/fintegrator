using System;

namespace SimpleIPaaS.Application.Models;

public record QueuedExecutionClaim(
    Guid ExecutionId,
    Guid FlowId,
    Guid TenantId,
    string TriggerSource,
    string? TriggerPayload,
    string FlowName = "",
    Guid? IntegrationId = null,
    string IntegrationName = "",
    string? TraceParent = null,
    bool IsTest = false,
    Guid? TestCaseId = null,
    Guid? TestRunId = null,
    DateTime? QueuedAt = null);