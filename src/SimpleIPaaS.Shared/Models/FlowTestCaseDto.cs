using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class FlowTestCaseDto
{
    public Guid Id { get; set; }
    public Guid FlowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool RequiredForPublish { get; set; }
    public FlowTestDefinitionDto Definition { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? LastPassedAt { get; set; }
}

public class FlowTestDefinitionDto
{
    public int Version { get; set; } = 2;
    public string TriggerPayloadJson { get; set; } = string.Empty;
    public Dictionary<string, List<FlowHttpResponseOverrideDto>> HttpResponses { get; set; } = new();
    public List<FlowCrossReferenceFixtureDto> CrossReferenceRows { get; set; } = new();
    public Dictionary<string, string> Variables { get; set; } = new();
    public List<FlowTestAssertionDto> Assertions { get; set; } = new();
    public List<FlowTestCardOverrideDto> CardOverrides { get; set; } = new();
    public string ResponseExhaustionBehavior { get; set; } = "RepeatFinalResponse";
}

public class FlowTestCardOverrideDto
{
    public Guid CardId { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string Mode { get; set; } = "Run";
    public string OutputFormat { get; set; } = "Json";
    public string OutputJson { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public class FlowHttpResponseOverrideDto
{
    public int StatusCode { get; set; } = 200;
    public string ResponseBody { get; set; } = "{}";
    public string ResponseFormat { get; set; } = "Json";
    public Dictionary<string, string[]> Headers { get; set; } = new();
}

public class FlowCrossReferenceFixtureDto
{
    public string ListName { get; set; } = string.Empty;
    public string KeyValue { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
}

public class FlowTestAssertionDto
{
    public string Type { get; set; } = "ExecutionStatus";
    public Guid? CardId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string Field { get; set; } = "Status";
    public string Path { get; set; } = string.Empty;
    public string Operator { get; set; } = "Equals";
    public string ExpectedValue { get; set; } = "Success";
    public string InvocationScope { get; set; } = "Every";
    public int? InvocationNumber { get; set; }
}

public class FlowTestRunDto
{
    public Guid Id { get; set; }
    public Guid TestCaseId { get; set; }
    public Guid FlowId { get; set; }
    public Guid FlowExecutionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ResultJson { get; set; } = "{}";
    public string? ErrorMessage { get; set; }
    public DateTime FlowUpdatedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class FlowTestEvaluationDto
{
    public bool Passed { get; set; }
    public List<FlowTestCheckResultDto> Checks { get; set; } = new();
    public List<FlowTestCardResultDto> Cards { get; set; } = new();
    public List<string> Failures { get; set; } = new();
}

public class FlowTestCheckResultDto
{
    public Guid? CardId { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ExpectedValue { get; set; } = string.Empty;
    public string ActualValue { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public int? Invocation { get; set; }
}

public class FlowTestCardResultDto
{
    public Guid CardId { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string Status { get; set; } = "Not reached";
    public bool IsMocked { get; set; }
    public int InvocationCount { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public long DurationMs { get; set; }
}