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
    public List<string> Warnings { get; set; } = new();
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
    public string ExpectedRunResult { get; set; } = "Success";
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
    public Guid? SuiteRunId { get; set; }
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
    public string ExecutionStatus { get; set; } = string.Empty;
    public string ExecutionError { get; set; } = string.Empty;
    public Guid? FailedCardId { get; set; }
    public string FailedCardName { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public List<FlowTestCheckResultDto> Checks { get; set; } = new();
    public List<FlowTestCardResultDto> Cards { get; set; } = new();
    public List<string> Failures { get; set; } = new();
}

public class FlowTestCheckResultDto
{
    public int CheckIndex { get; set; }
    public Guid? CardId { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string ExpectedValue { get; set; } = string.Empty;
    public string ActualValue { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public int? Invocation { get; set; }
    public bool IsJsonComparison { get; set; }
}

public class FlowTestCardResultDto
{
    public Guid CardId { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string Status { get; set; } = "NotReached";
    public int Order { get; set; }
    public DateTime? StartedAt { get; set; }
    public bool IsMocked { get; set; }
    public int InvocationCount { get; set; }
    public string Input { get; set; } = string.Empty;
    public bool InputTruncated { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public int FailedCheckCount { get; set; }
    public long DurationMs { get; set; }
}

public class FlowTestRunDetailDto
{
    public FlowTestRunDto Run { get; set; } = new();
    public FlowTestEvaluationDto? Evaluation { get; set; }
    public List<FlowTestCardProgressDto> Progress { get; set; } = new();
}

public class FlowTestCardProgressDto
{
    public Guid CardId { get; set; }
    public string CardName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class FlowTestRequirementStateDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "NeverRun";
}

public class FlowTestSuiteStatusDto
{
    public List<FlowTestRequirementStateDto> RequiredTests { get; set; } = new();
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Stale { get; set; }
    public int NeverRun { get; set; }
    public string State { get; set; } = "NeverRun";
    public int Total { get; set; }
    public int ScenarioCount { get; set; }
}

public class FlowTestSuiteStartDto
{
    public Guid SuiteRunId { get; set; }
    public List<Guid> TestRunIds { get; set; } = new();
}

public class FlowTestSuiteRunRequestDto
{
    public bool RequiredOnly { get; set; }
}

public class FlowTestSuiteRunDto
{
    public Guid SuiteRunId { get; set; }
    public string Status { get; set; } = "Running";
    public List<FlowTestSuiteScenarioDto> Scenarios { get; set; } = new();
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Running { get; set; }
}

public class FlowTestSuiteScenarioDto
{
    public Guid TestRunId { get; set; }
    public Guid TestCaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string? Error { get; set; }
}