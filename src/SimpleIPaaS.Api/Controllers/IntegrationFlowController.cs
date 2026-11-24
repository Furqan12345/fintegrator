using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/integrationflow")]
public class IntegrationFlowController : ControllerBase
{
    private static readonly HashSet<string> NonSecretAuthConfigKeys = new(StringComparer.OrdinalIgnoreCase) { "placement" };

    private readonly IIntegrationRepository _repository;
    private readonly IAdvancedCodeExecutionService _codeExecutionService;
    private readonly FlowRunService _flowRunService;
    private readonly FlowExecutor _flowExecutor;
    private readonly IEncryptionService _encryptionService;
    private readonly IFlowTestRepository _flowTestRepository;
    private readonly IExecutionRepository _executionRepository;
    private readonly FlowTestRunner _flowTestRunner;
    private readonly ILogger<IntegrationFlowController> _logger;

    public IntegrationFlowController(
        IIntegrationRepository repository,
        IAdvancedCodeExecutionService codeExecutionService,
        FlowRunService flowRunService,
        FlowExecutor flowExecutor,
        IEncryptionService encryptionService,
        IFlowTestRepository flowTestRepository,
        IExecutionRepository executionRepository,
        FlowTestRunner flowTestRunner,
        ILogger<IntegrationFlowController> logger)
    {
        _repository = repository;
        _codeExecutionService = codeExecutionService;
        _flowRunService = flowRunService;
        _flowExecutor = flowExecutor;
        _encryptionService = encryptionService;
        _flowTestRepository = flowTestRepository;
        _executionRepository = executionRepository;
        _flowTestRunner = flowTestRunner;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetAll([FromQuery] bool lite = false)
    {
        // lite=true is the list-page view: no scripts, step configuration or edges (see GetAllSummariesAsync).
        var flows = lite ? await _repository.GetAllSummariesAsync() : await _repository.GetAllAsync();
        return Ok(flows.Select(flow => flow.ToDto()));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var flow = await _repository.GetByIdAsync(id);
        if (flow == null) return NotFound();

        return Ok(flow.ToDto());
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] IntegrationFlowDto dto)
    {
        var validationErrors = FlowValidator.Validate(dto);
        if (validationErrors.Count > 0)
        {
            return FlowValidationProblem(validationErrors);
        }

        var helperError = await ValidateHelperAsync(dto.HelperCode);
        if (helperError != null)
        {
            return FlowValidationProblem(new List<string> { helperError });
        }

        var flow = dto.ToEntity();
        flow.Status = FlowStatus.Draft;
        flow.PublishedVersion = null;
        flow.LastPublishedAt = null;
        flow.Id = Guid.NewGuid();
        flow.WebhookSecret = IssueWebhookSecret(flow.TriggerType);
        await ProtectStepSecretsAsync(flow);
        await _repository.AddAsync(flow);
        return CreatedAtAction(nameof(Get), new { id = flow.Id }, flow.ToDto());
    }

    [HttpPut("{flowId}")]
    public async Task<IActionResult> Update([FromRoute] Guid flowId, [FromBody] IntegrationFlowDto dto)
    {
        if (flowId != dto.Id) return BadRequest("ID mismatch");

        var existingFlow = await _repository.GetByIdAsync(flowId);
        if (existingFlow == null) return NotFound();

        // Older clients do not send helperCode. Null means "keep what is stored"; the designer
        // sends an empty string when the operator deliberately clears it.
        dto.HelperCode ??= existingFlow.HelperCode;

        var validationErrors = FlowValidator.Validate(dto);
        if (validationErrors.Count > 0)
        {
            return FlowValidationProblem(validationErrors);
        }

        var helperError = await ValidateHelperAsync(dto.HelperCode);
        if (helperError != null)
        {
            return FlowValidationProblem(new List<string> { helperError });
        }

        var renameWarnings = FlowRenameCheck.FindStaleReferences(existingFlow, dto);

        var flow = dto.ToEntity();
        flow.WebhookSecret = IssueWebhookSecret(flow.TriggerType);
        await ProtectStepSecretsAsync(flow);

        if (!await _repository.UpdateAsync(flow))
        {
            return NotFound();
        }

        var updated = await _repository.GetByIdAsync(flowId);
        if (updated == null)
        {
            return NotFound();
        }

        var result = updated.ToDto();
        result.Warnings = renameWarnings;
        return Ok(result);
    }

    [HttpGet("{flowId}/versions")]
    public async Task<IActionResult> GetVersions([FromRoute] Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null)
        {
            return NotFound();
        }

        var versions = await _repository.GetVersionsAsync(flowId);
        return Ok(versions.Select(version => new FlowVersionDto
        {
            VersionNumber = version.VersionNumber,
            CreatedAt = version.CreatedAt,
            ChangeNote = version.ChangeNote,
            RolledBackFromVersion = version.RolledBackFromVersion,
            IsCurrent = flow.PublishedVersion == version.VersionNumber
        }));
    }

    [HttpPost("{flowId}/publish")]
    public async Task<IActionResult> Publish([FromRoute] Guid flowId, [FromBody] FlowVersionActionDto? action = null)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null)
        {
            return NotFound();
        }

        var validationErrors = FlowValidator.Validate(flow.ToDto());
        if (validationErrors.Count > 0)
        {
            return FlowValidationProblem(validationErrors);
        }

        var suiteStatus = await BuildSuiteStatusAsync(flow);
        var blocked = suiteStatus.RequiredTests
            .Where(test => !test.State.Equals("Passed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (blocked.Count > 0)
        {
            return Problem(
                title: "Required tests must pass before publishing",
                detail: "Run the required tests against the current draft and fix any failures.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["requiredTests"] = blocked });
        }

        var version = await _repository.PublishAsync(flowId, action?.ChangeNote);
        if (version == null)
        {
            return NotFound();
        }

        var published = await _repository.GetByIdAsync(flowId);
        return published == null ? NotFound() : Ok(published.ToDto());
    }

    [HttpPost("{flowId}/rollback/{versionNumber:int}")]
    public async Task<IActionResult> Rollback(
        [FromRoute] Guid flowId,
        [FromRoute] int versionNumber,
        [FromBody] FlowVersionActionDto? action = null)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null)
        {
            return Problem(
                title: "Flow not found",
                detail: "This flow no longer exists, or it belongs to another workspace.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (versionNumber < 1)
        {
            return Problem(
                title: "Invalid version",
                detail: "Flow versions start at 1.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var versions = await _repository.GetVersionsAsync(flowId);
        if (versions.All(version => version.VersionNumber != versionNumber))
        {
            var available = versions.Select(version => version.VersionNumber).OrderBy(number => number).ToList();
            return Problem(
                title: "Version not found",
                detail: available.Count == 0
                    ? $"Flow '{flow.Name}' has no published versions yet. Publish the flow first."
                    : $"Version {versionNumber} does not exist for flow '{flow.Name}'. Published versions: {string.Join(", ", available)}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var rolledBack = await _repository.RollbackAsync(flowId, versionNumber, action?.ChangeNote);
            if (rolledBack == null)
            {
                return Problem(
                    title: "Version cannot be restored",
                    detail: $"Version {versionNumber} could not be restored because its stored snapshot is unreadable.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Ok(rolledBack.ToDto());
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Rollback of flow {FlowId} to version {VersionNumber} failed.", flowId, versionNumber);
            return Problem(
                title: "Rollback could not be applied",
                detail: "The stored version could not be restored because the flow changed while it was being restored. Reload the flow and try again.",
                statusCode: StatusCodes.Status409Conflict);
        }
    }
    [HttpPost("{flowId}/webhook-secret")]
    public async Task<IActionResult> RegenerateWebhookSecret([FromRoute] Guid flowId)
    {
        if (!await _repository.UpdateWebhookSecretAsync(flowId, GenerateWebhookSecret()))
        {
            return NotFound();
        }

        var flow = await _repository.GetByIdAsync(flowId);
        return flow == null ? NotFound() : Ok(flow.ToDto());
    }

    [HttpDelete("{flowId}")]
    public async Task<IActionResult> Delete([FromRoute] Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) return NotFound();

        try
        {
            await _repository.DeleteAsync(flowId);
        }
        catch (DbUpdateException)
        {
            return Problem(
                title: "Conflict",
                detail: "The flow could not be deleted because dependent records exist.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return NoContent();
    }

    [HttpPost("{flowId}/schedule/start")]
    public async Task<IActionResult> StartSchedule([FromRoute] Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) return NotFound();

        if (!HasScheduledTrigger(flow))
        {
            return Problem(
                title: "No schedule",
                detail: "This flow has no cron or one-time schedule to start.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (flow.PublishedVersion == null || flow.Status == FlowStatus.Draft)
        {
            return Problem(
                title: "Publish required",
                detail: "Publish the flow before starting its schedule.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (flow.RunAt is DateTime runAt && runAt <= DateTime.UtcNow)
        {
            return Problem(
                title: "Schedule is in the past",
                detail: "This one-time schedule has already passed. Update its run time and publish again.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var updated = await _repository.SetScheduleStatusAsync(flowId, FlowStatus.Active);
        return updated == null ? NotFound() : Ok(updated.ToDto());
    }

    [HttpPost("{flowId}/schedule/pause")]
    public async Task<IActionResult> PauseSchedule([FromRoute] Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) return NotFound();

        if (!HasScheduledTrigger(flow))
        {
            return Problem(
                title: "No schedule",
                detail: "This flow has no cron or one-time schedule to pause.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (flow.Status != FlowStatus.Active)
        {
            return Ok(flow.ToDto());
        }

        var updated = await _repository.SetScheduleStatusAsync(flowId, FlowStatus.Paused);
        return updated == null ? NotFound() : Ok(updated.ToDto());
    }

    private static bool HasScheduledTrigger(Domain.Entities.IntegrationFlow flow) =>
        (flow.TriggerType == TriggerType.Cron &&
         (!string.IsNullOrWhiteSpace(flow.CronExpression) || flow.RunAt.HasValue)) ||
        flow.Nodes.Any(node => node.StepType == StepType.Schedule);

    [HttpPost("{flowId}/run")]
    public async Task<IActionResult> Run([FromRoute] Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) return NotFound();

        var executionId = await _flowRunService.EnqueueAsync(flowId, "Manual", null, HttpContext.RequestAborted);
        return Accepted(new { success = true, executionId, status = ExecutionStatus.Queued.ToString() });
    }

    [HttpPost("{flowId}/debug/{stepId:guid}")]
    public async Task<IActionResult> RunDebugNode(
        [FromRoute] Guid flowId,
        [FromRoute] Guid stepId,
        [FromBody] DebugNodeRunRequestDto? request)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) return NotFound();
        if (flow.Nodes.All(node => node.Id != stepId)) return NotFound();

        Newtonsoft.Json.Linq.JObject flowState;
        try
        {
            flowState = Newtonsoft.Json.Linq.JObject.Parse(
                string.IsNullOrWhiteSpace(request?.FlowStateJson) ? "{}" : request!.FlowStateJson);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            ModelState.AddModelError(nameof(DebugNodeRunRequestDto.FlowStateJson), "flowStateJson must be a JSON object.");
            return ValidationProblem(ModelState);
        }

        try
        {
            var result = await _flowExecutor.ExecuteDebugNodeAsync(
                flowId, stepId, flowState, request?.PersistedStateJson, HttpContext.RequestAborted);

            return Ok(new DebugNodeRunResultDto
            {
                ExecutionId = result.ExecutionId,
                StepId = result.StepId,
                StepExecutionId = result.StepExecutionId,
                Status = result.Status.ToString(),
                DurationMs = result.DurationMs,
                Input = result.Input,
                Output = result.Output,
                Error = result.Error,
                BranchResult = result.BranchResult,
                HttpStatusCode = result.HttpStatusCode,
                ErrorPort = result.ErrorPort
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    [HttpGet("{flowId}/test-cases")]
    public async Task<IActionResult> GetTestCases([FromRoute] Guid flowId)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        var tests = await _flowTestRepository.GetByFlowIdAsync(flowId);
        return Ok(tests.Select(ToTestCaseDto));
    }

    [HttpPost("{flowId}/test-cases")]
    public async Task<IActionResult> CreateTestCase([FromRoute] Guid flowId, [FromBody] FlowTestCaseDto dto)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("A test case name is required.");
        var testCase = new Domain.Entities.FlowTestCase
        {
            Id = Guid.NewGuid(),
            FlowId = flowId,
            Name = dto.Name.Trim(),
            Description = dto.Description ?? string.Empty,
            Enabled = dto.Enabled,
            RequiredForPublish = dto.RequiredForPublish,
            DefinitionJson = JsonSerializer.Serialize(dto.Definition ?? new FlowTestDefinitionDto()),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _flowTestRepository.AddAsync(testCase);
        return Ok(ToTestCaseDto(testCase));
    }

    [HttpPut("{flowId}/test-cases/{testCaseId}")]
    public async Task<IActionResult> UpdateTestCase([FromRoute] Guid flowId, [FromRoute] Guid testCaseId, [FromBody] FlowTestCaseDto dto)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        var testCase = await _flowTestRepository.GetAsync(testCaseId);
        if (testCase == null || testCase.FlowId != flowId) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("A test case name is required.");
        testCase.Name = dto.Name.Trim();
        testCase.Description = dto.Description ?? string.Empty;
        testCase.Enabled = dto.Enabled;
        testCase.RequiredForPublish = dto.RequiredForPublish;
        testCase.DefinitionJson = JsonSerializer.Serialize(dto.Definition ?? new FlowTestDefinitionDto());
        if (!await _flowTestRepository.UpdateAsync(testCase)) return NotFound();
        var updated = await _flowTestRepository.GetAsync(testCaseId);
        return updated == null ? NotFound() : Ok(ToTestCaseDto(updated));
    }

    [HttpDelete("{flowId}/test-cases/{testCaseId}")]
    public async Task<IActionResult> DeleteTestCase([FromRoute] Guid flowId, [FromRoute] Guid testCaseId)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        var testCase = await _flowTestRepository.GetAsync(testCaseId);
        if (testCase == null || testCase.FlowId != flowId) return NotFound();
        return await _flowTestRepository.DeleteAsync(testCaseId) ? NoContent() : NotFound();
    }

    [HttpGet("{flowId}/test-cases/{testCaseId}/runs")]
    public async Task<IActionResult> GetTestRuns([FromRoute] Guid flowId, [FromRoute] Guid testCaseId)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        var testCase = await _flowTestRepository.GetAsync(testCaseId);
        if (testCase == null || testCase.FlowId != flowId) return NotFound();
        var runs = await _flowTestRepository.GetRunsAsync(testCaseId);
        return Ok(runs.Select(ToTestRunDto));
    }

    [HttpPost("{flowId}/test-cases/{testCaseId}/run")]
    public async Task<IActionResult> RunTestCase([FromRoute] Guid flowId, [FromRoute] Guid testCaseId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        var testCase = await _flowTestRepository.GetAsync(testCaseId);
        if (flow == null || testCase == null || testCase.FlowId != flowId) return NotFound();
        if (!testCase.Enabled) return BadRequest("This test case is disabled.");

        try
        {
            var (testRunId, executionId) = await _flowTestRunner.StartAsync(
                flowId, testCase.Id, cancellationToken: HttpContext.RequestAborted);
            return Accepted(new
            {
                success = true,
                testRunId,
                executionId,
                status = "Running"
            });
        }
        catch (Exception ex)
        {
            return Problem(title: "Test could not start", detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet("{flowId}/test-runs/{testRunId:guid}")]
    public async Task<IActionResult> GetTestRunDetail(
        [FromRoute] Guid flowId,
        [FromRoute] Guid testRunId)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        var run = await _flowTestRepository.GetRunAsync(testRunId);
        if (run == null || run.FlowId != flowId) return NotFound();

        FlowTestEvaluationDto? evaluation = null;
        if (!string.IsNullOrWhiteSpace(run.ResultJson) && run.ResultJson != "{}")
        {
            try
            {
                evaluation = JsonSerializer.Deserialize<FlowTestEvaluationDto>(run.ResultJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                // Leave the raw run available even if an older result cannot be parsed.
            }
        }

        var progress = new List<FlowTestCardProgressDto>();
        if (run.FlowExecutionId != Guid.Empty)
        {
            var steps = await _executionRepository.GetStepExecutionsAsync(run.FlowExecutionId);
            progress = steps.GroupBy(step => step.StepId).Select(group =>
            {
                var last = group.OrderBy(step => step.StartedAt).Last();
                return new FlowTestCardProgressDto
                {
                    CardId = group.Key,
                    CardName = last.NodeName,
                    Status = last.Status.ToString(),
                    StartedAt = group.Min(step => step.StartedAt),
                    CompletedAt = last.CompletedAt,
                    ErrorMessage = last.ErrorMessage
                };
            }).OrderBy(item => item.StartedAt).ToList();
        }

        return Ok(new FlowTestRunDetailDto
        {
            Run = ToTestRunDto(run),
            Evaluation = evaluation,
            Progress = progress
        });
    }

    [HttpPost("{flowId}/test-suite/run")]
    public async Task<IActionResult> RunTestSuite(
        [FromRoute] Guid flowId,
        [FromBody] FlowTestSuiteRunRequestDto? request)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        try
        {
            var result = await _flowTestRunner.StartSuiteAsync(
                flowId, request?.RequiredOnly ?? false, HttpContext.RequestAborted);
            return Accepted(result);
        }
        catch (Exception ex)
        {
            return Problem(title: "Test suite could not start", detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [HttpGet("{flowId}/test-suite/runs/{suiteRunId:guid}")]
    public async Task<IActionResult> GetTestSuiteRun(
        [FromRoute] Guid flowId,
        [FromRoute] Guid suiteRunId)
    {
        if (await _repository.GetByIdAsync(flowId) == null) return NotFound();
        var runs = await _flowTestRepository.GetRunsBySuiteAsync(suiteRunId);
        if (runs.Count == 0) return NotFound();

        var tests = (await _flowTestRepository.GetByFlowIdAsync(flowId)).ToDictionary(test => test.Id);
        var scenarios = runs.Select(run =>
        {
            var headline = string.Empty;
            if (!string.IsNullOrWhiteSpace(run.ResultJson) && run.ResultJson != "{}")
            {
                try
                {
                    var evaluation = JsonSerializer.Deserialize<FlowTestEvaluationDto>(run.ResultJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    headline = evaluation?.Headline ?? string.Empty;
                }
                catch (JsonException)
                {
                    // Keep the run visible even if an old result cannot be parsed.
                }
            }

            return new FlowTestSuiteScenarioDto
            {
                TestRunId = run.Id,
                TestCaseId = run.TestCaseId,
                Name = tests.TryGetValue(run.TestCaseId, out var test) ? test.Name : "Unknown scenario",
                Status = run.Status,
                Headline = headline,
                Error = run.ErrorMessage
            };
        }).ToList();

        return Ok(new FlowTestSuiteRunDto
        {
            SuiteRunId = suiteRunId,
            Scenarios = scenarios,
            Passed = scenarios.Count(scenario => scenario.Status == "Passed"),
            Failed = scenarios.Count(scenario => scenario.Status == "Failed"),
            Running = scenarios.Count(scenario => scenario.Status is "Queued" or "Running"),
            Status = scenarios.Any(scenario => scenario.Status is "Queued" or "Running")
                ? "Running"
                : scenarios.Any(scenario => scenario.Status == "Failed") ? "Failed" : "Passed"
        });
    }

    [HttpGet("{flowId}/test-suite/status")]
    public async Task<IActionResult> GetTestSuiteStatus([FromRoute] Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        return flow == null ? NotFound() : Ok(await BuildSuiteStatusAsync(flow));
    }

    [HttpPost("{flowId}/test-cases/from-execution/{executionId:guid}")]
    public async Task<IActionResult> CreateTestCaseFromExecution(
        [FromRoute] Guid flowId,
        [FromRoute] Guid executionId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) return NotFound();
        var execution = await _executionRepository.GetFlowExecutionAsync(executionId);
        if (execution == null || execution.FlowId != flowId)
            return BadRequest("The transaction does not belong to this flow.");
        if (execution.IsTest)
            return BadRequest("A test run cannot be used as the source of a scenario.");

        var steps = (await _executionRepository.GetStepExecutionsAsync(executionId))
            .OrderBy(step => step.StartedAt).ToList();
        var warnings = new List<string>();
        var definition = new FlowTestDefinitionDto
        {
            Version = 2,
            TriggerPayloadJson = execution.TriggerPayloadJson ?? "{}",
            ResponseExhaustionBehavior = "FailWhenExhausted",
            ExpectedRunResult = execution.Status == ExecutionStatus.Failed ? "Failed" : "Success"
        };

        var nodeById = flow.Nodes.ToDictionary(node => node.Id);
        var groups = steps.GroupBy(step => step.StepId)
            .OrderBy(group => group.Min(step => step.StartedAt)).ToList();

        foreach (var group in groups)
        {
            nodeById.TryGetValue(group.Key, out var node);
            if (node?.StepType == StepType.HttpAction)
            {
                var fixtures = new List<FlowHttpResponseOverrideDto>();
                foreach (var step in group.OrderBy(step => step.StartedAt))
                {
                    var packets = (await _executionRepository.GetStepPacketLogsAsync(step.Id))
                        .OrderBy(packet => packet.Sequence).ToList();
                    var packet = packets.LastOrDefault(candidate =>
                        candidate.StatusCode.HasValue || !string.IsNullOrWhiteSpace(candidate.ResponseBody));
                    var body = !string.IsNullOrWhiteSpace(packet?.ResponseBody)
                        ? packet!.ResponseBody
                        : step.ResponsePayload;
                    if (body.Length >= 256 * 1024)
                        warnings.Add($"{step.NodeName} invocation {fixtures.Count + 1} was stored at the payload limit; its fixture may be incomplete.");

                    fixtures.Add(new FlowHttpResponseOverrideDto
                    {
                        StatusCode = packet?.StatusCode ?? step.HttpStatusCode,
                        ResponseBody = body,
                        ResponseFormat = IsJson(body) ? "Json" : "Text",
                        Headers = RedactHeaders(packet?.ResponseHeadersJson)
                    });
                }

                definition.HttpResponses[node.Id.ToString()] = fixtures;
            }
            else if (node?.StepType == StepType.FanOut)
            {
                var last = group.OrderBy(step => step.StartedAt).Last();
                definition.CardOverrides.Add(new FlowTestCardOverrideDto
                {
                    CardId = node.Id,
                    CardName = node.NodeName,
                    Mode = "Supply",
                    OutputFormat = "Json",
                    OutputJson = last.ResponsePayload
                });
                warnings.Add($"{node.NodeName} was recorded as a supplied output because Fan Out cannot run in isolation.");
            }
        }

        definition.Assertions.Add(new FlowTestAssertionDto
        {
            Type = "ExecutionStatus",
            Field = "Status",
            Operator = "Equals",
            ExpectedValue = execution.Status.ToString(),
            InvocationScope = "Every"
        });

        foreach (var group in groups)
        {
            var last = group.OrderBy(step => step.StartedAt).Last();
            definition.Assertions.Add(new FlowTestAssertionDto
            {
                Type = "CardStatus",
                CardId = group.Key,
                NodeName = last.NodeName,
                Field = "Status",
                Operator = "Equals",
                ExpectedValue = last.Status.ToString(),
                InvocationScope = "Every"
            });
        }

        if (groups.Count > 0)
        {
            var lastGroup = groups[^1];
            var last = lastGroup.OrderBy(step => step.StartedAt).Last();
            definition.Assertions.Add(new FlowTestAssertionDto
            {
                Type = "OutputField",
                CardId = lastGroup.Key,
                NodeName = last.NodeName,
                Field = "ResponsePayload",
                Operator = "Equals",
                ExpectedValue = last.ResponsePayload,
                InvocationScope = "Every"
            });
        }

        var label = string.IsNullOrWhiteSpace(execution.Identifier)
            ? execution.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
            : execution.Identifier!;
        return Ok(new FlowTestCaseDto
        {
            Id = Guid.Empty,
            FlowId = flowId,
            Name = $"From transaction {executionId.ToString()[..8]} ({label})",
            Description = $"Built from {executionId}",
            Enabled = true,
            RequiredForPublish = false,
            Definition = definition,
            Warnings = warnings
        });
    }

    private static bool IsJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { using var _ = JsonDocument.Parse(value); return true; }
        catch (JsonException) { return false; }
    }

    private static Dictionary<string, string[]> RedactHeaders(string? json)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            if (root == null) return result;
            foreach (var property in root)
            {
                var name = property.Key;
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("api-key", StringComparison.OrdinalIgnoreCase))
                    continue;
                result[name] = property.Value is System.Text.Json.Nodes.JsonArray array
                    ? array.Select(item => item?.ToString() ?? string.Empty).ToArray()
                    : new[] { property.Value?.ToString() ?? string.Empty };
            }
        }
        catch (JsonException)
        {
            // Invalid header JSON is treated as no headers.
        }
        return result;
    }

    private async Task<FlowTestSuiteStatusDto> BuildSuiteStatusAsync(Domain.Entities.IntegrationFlow flow)
    {
        var allTests = (await _flowTestRepository.GetByFlowIdAsync(flow.Id)).ToList();
        var required = allTests
            .Where(test => test.Enabled && test.RequiredForPublish)
            .ToList();
        var states = new List<FlowTestRequirementStateDto>();
        foreach (var test in required)
        {
            var latest = await _flowTestRepository.GetLatestRunAsync(test.Id);
            var state = latest == null
                ? "NeverRun"
                : !latest.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                    ? "Failed"
                    : latest.FlowUpdatedAt < flow.UpdatedAt || latest.StartedAt < test.UpdatedAt
                        ? "Stale"
                        : "Passed";
            states.Add(new FlowTestRequirementStateDto { Id = test.Id, Name = test.Name, State = state });
        }

        return new FlowTestSuiteStatusDto
        {
            RequiredTests = states,
            Total = states.Count,
            ScenarioCount = allTests.Count,
            Passed = states.Count(item => item.State == "Passed"),
            Failed = states.Count(item => item.State == "Failed"),
            Stale = states.Count(item => item.State == "Stale"),
            NeverRun = states.Count(item => item.State == "NeverRun"),
            State = states.Any(item => item.State == "Failed")
                ? "Failed"
                : states.Any(item => item.State == "Stale")
                    ? "Stale"
                    : states.Any(item => item.State == "NeverRun") ? "NeverRun" : "Passed"
        };
    }
    [HttpPost("test-mapping")]
    [RequestSizeLimit(262_144)]
    [EnableRateLimiting("test-mapping")]
    public async Task<IActionResult> TestMapping([FromBody] TestMappingRequestDto request)
    {
        if (request.MappingCode.Length > 24_000)
        {
            return BadRequest("The script exceeds the 24,000-character limit.");
        }

        if ((request.HelperCode?.Length ?? 0) > 64_000)
        {
            return BadRequest("The helper script exceeds the 64,000-character limit.");
        }

        try
        {
            string result;
            if (request.StepType == "Branch")
            {
                var branchResult = await _codeExecutionService.ExecuteBranchAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson, request.HelperCode);
                result = branchResult.ToString().ToLower();
            }
            else if (request.TargetProperty == "PostFlightCode")
            {
                result = await _codeExecutionService.ExecutePostFlightAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson, request.HttpResponseJson, request.HelperCode);
            }
            else if (request.TargetProperty == "UrlCode")
            {
                result = await _codeExecutionService.ExecuteUrlAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson, request.HelperCode);
            }
            else
            {
                result = await _codeExecutionService.ExecuteMappingAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson, request.HelperCode);
            }

            return Ok(new TestMappingResponseDto
            {
                Success = true,
                Result = result
            });
        }
        catch (Exception ex)
        {
            return Ok(new TestMappingResponseDto
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    [HttpPost("cron-preview")]
    public IActionResult CronPreview([FromBody] CronPreviewRequestDto request)
    {
        if (!TryBuildCronPreview(request.CronExpression, request.Count, out var preview, out var error))
        {
            return Problem(
                title: "Invalid cron expression",
                detail: error,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Ok(preview);
    }

    [HttpGet("{flowId}/persisted-state")]
    public async Task<IActionResult> GetPersistedState([FromRoute] Guid flowId)
    {
        if (await _repository.GetByIdAsync(flowId) == null)
        {
            return FlowNotFound(flowId);
        }

        var persistedStateJson = await _repository.GetPersistedStateAsync(flowId);
        return Ok(new PersistedStateDto { FlowId = flowId, PersistedStateJson = persistedStateJson });
    }

    [HttpPut("{flowId}/persisted-state")]
    public async Task<IActionResult> UpdatePersistedState([FromRoute] Guid flowId, [FromBody] PersistedStateDto dto)
    {
        if (await _repository.GetByIdAsync(flowId) == null)
        {
            return FlowNotFound(flowId);
        }

        await _repository.UpdatePersistedStateAsync(flowId, dto.PersistedStateJson);
        return Ok(new PersistedStateDto { FlowId = flowId, PersistedStateJson = dto.PersistedStateJson });
    }

    [HttpDelete("{flowId}/persisted-state")]
    public async Task<IActionResult> ResetPersistedState([FromRoute] Guid flowId)
    {
        if (await _repository.GetByIdAsync(flowId) == null)
        {
            return FlowNotFound(flowId);
        }

        await _repository.UpdatePersistedStateAsync(flowId, "{}");
        return NoContent();
    }

    private ObjectResult FlowNotFound(Guid flowId) => Problem(
        title: "Flow not found",
        detail: $"No flow with id {flowId} exists in this workspace.",
        statusCode: StatusCodes.Status404NotFound);

    public static bool TryBuildCronPreview(string cronExpression, int count, out CronPreviewResponseDto preview, out string error)
    {
        preview = new CronPreviewResponseDto();
        var expression = (cronExpression ?? string.Empty).Trim();

        if (expression.Length == 0)
        {
            error = "Enter a cron expression: minute hour day-of-month month day-of-week, with an optional leading seconds field.";
            return false;
        }

        CronExpression parsed;
        try
        {
            parsed = ParseCronExpression(expression);
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }

        var from = DateTime.UtcNow;
        preview.CronExpression = expression;
        preview.Description = DescribeCron(expression);
        preview.NextOccurrences = parsed
            .GetOccurrences(from, from.AddYears(5), fromInclusive: false)
            .Take(Math.Clamp(count <= 0 ? 3 : count, 1, 10))
            .ToList();

        error = string.Empty;
        return true;
    }

    private static CronExpression ParseCronExpression(string expression)
    {
        try
        {
            return CronExpression.Parse(expression);
        }
        catch (CronFormatException)
        {
            return CronExpression.Parse(expression, CronFormat.IncludeSeconds);
        }
    }

    private static string DescribeCron(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            return $"Runs on the {parts[0]} schedule.";
        }

        var units = parts.Length == 6
            ? new[] { "second", "minute", "hour", "day", "month", "weekday" }
            : new[] { "minute", "hour", "day", "month", "weekday" };

        if (parts.Length != units.Length)
        {
            return string.Empty;
        }

        return $"Runs {string.Join(", ", parts.Select((part, index) => DescribeCronField(part, units[index])))}.";
    }

    private static string DescribeCronField(string value, string unit)
    {
        if (value is "*" or "?")
        {
            return $"every {unit}";
        }

        return value.StartsWith("*/", StringComparison.Ordinal)
            ? $"every {value[2..]} {unit}s"
            : $"at {unit} {value}";
    }

    private static FlowTestCaseDto ToTestCaseDto(Domain.Entities.FlowTestCase testCase)
    {
        FlowTestDefinitionDto definition;
        try
        {
            definition = JsonSerializer.Deserialize<FlowTestDefinitionDto>(testCase.DefinitionJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FlowTestDefinitionDto();
        }
        catch (JsonException)
        {
            definition = new FlowTestDefinitionDto();
        }
        return new FlowTestCaseDto
        {
            Id = testCase.Id,
            FlowId = testCase.FlowId,
            Name = testCase.Name,
            Description = testCase.Description,
            Enabled = testCase.Enabled,
            RequiredForPublish = testCase.RequiredForPublish,
            Definition = definition,
            CreatedAt = testCase.CreatedAt,
            UpdatedAt = testCase.UpdatedAt,
            LastRunAt = testCase.LastRunAt,
            LastPassedAt = testCase.LastPassedAt
        };
    }

    private static FlowTestRunDto ToTestRunDto(Domain.Entities.FlowTestRun run) => new()
    {
        Id = run.Id,
        TestCaseId = run.TestCaseId,
        FlowId = run.FlowId,
        SuiteRunId = run.SuiteRunId,
        FlowExecutionId = run.FlowExecutionId,
        Status = run.Status,
        ResultJson = run.ResultJson,
        ErrorMessage = run.ErrorMessage,
        FlowUpdatedAt = run.FlowUpdatedAt,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt
    };
    [HttpPost("validate-helper")]
    public async Task<IActionResult> ValidateHelperScript([FromBody] HelperScriptDto request)
    {
        var error = await ValidateHelperAsync(request.HelperCode);
        return error == null
            ? Ok(new { valid = true })
            : FlowValidationProblem(new List<string> { error });
    }

    private async Task<string?> ValidateHelperAsync(string? helperCode)
    {
        if (string.IsNullOrWhiteSpace(helperCode))
        {
            return null;
        }

        if (helperCode.Length > 64_000)
        {
            return "Helper script is too large (limit 64,000 characters).";
        }

        try
        {
            await _codeExecutionService.ValidateAsync(helperCode, HttpContext?.RequestAborted ?? CancellationToken.None);
            return null;
        }
        catch (ScriptExecutionException ex)
        {
            return ex.Message;
        }
    }

    private IActionResult FlowValidationProblem(List<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError("flow", error);
        }

        return ValidationProblem(ModelState);
    }

    private static string IssueWebhookSecret(TriggerType triggerType)
    {
        return triggerType == TriggerType.Webhook ? GenerateWebhookSecret() : string.Empty;
    }

    private static string GenerateWebhookSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private async Task ProtectStepSecretsAsync(Domain.Entities.IntegrationFlow flow)
    {
        foreach (var node in flow.Nodes)
        {
            node.AuthToken = await ProtectAsync(node.AuthToken);
            node.AuthUsername = await ProtectAsync(node.AuthUsername);
            node.AuthPassword = await ProtectAsync(node.AuthPassword);
            node.AuthConfigJson = await ProtectAsync(StripBlankAuthConfig(node.AuthConfigJson));
        }
    }

    private async Task<string> ProtectAsync(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : await _encryptionService.EncryptAsync(value);
    }

    private static string StripBlankAuthConfig(string authConfigJson)
    {
        if (string.IsNullOrWhiteSpace(authConfigJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(authConfigJson);
            return CarriesAuthValue(document.RootElement, null) ? authConfigJson : string.Empty;
        }
        catch (JsonException)
        {
            return authConfigJson;
        }
    }

    private static bool CarriesAuthValue(JsonElement element, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (CarriesAuthValue(property.Value, property.Name))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (CarriesAuthValue(item, propertyName))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.String:
                return !NonSecretAuthConfigKeys.Contains(propertyName ?? string.Empty) &&
                       !string.IsNullOrWhiteSpace(element.GetString());
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;
            default:
                return true;
        }
    }
}
