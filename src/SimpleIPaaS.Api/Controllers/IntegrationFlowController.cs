using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Cronos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Infrastructure.MultiTenancy;
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
    private readonly ITenantContext _tenantContext;
    private readonly IEncryptionService _encryptionService;

    public IntegrationFlowController(
        IIntegrationRepository repository,
        IAdvancedCodeExecutionService codeExecutionService,
        FlowRunService flowRunService,
        ITenantContext tenantContext,
        IEncryptionService encryptionService)
    {
        _repository = repository;
        _codeExecutionService = codeExecutionService;
        _flowRunService = flowRunService;
        _tenantContext = tenantContext;
        _encryptionService = encryptionService;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetAll()
    {
        var flows = await _repository.GetAllAsync();
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

        var flow = dto.ToEntity();
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

        var validationErrors = FlowValidator.Validate(dto);
        if (validationErrors.Count > 0)
        {
            return FlowValidationProblem(validationErrors);
        }

        var flow = dto.ToEntity();
        flow.WebhookSecret = IssueWebhookSecret(flow.TriggerType);
        await ProtectStepSecretsAsync(flow);

        if (!await _repository.UpdateAsync(flow))
        {
            return NotFound();
        }

        var updated = await _repository.GetByIdAsync(flowId);
        return updated == null ? NotFound() : Ok(updated.ToDto());
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

    [HttpPost("{flowId}/run")]
    public async Task<IActionResult> Run([FromRoute] Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) return NotFound();

        var executionId = await _flowRunService.EnqueueAsync(flowId, _tenantContext.TenantId, "Manual", null, HttpContext.RequestAborted);
        return Accepted(new { success = true, executionId, status = ExecutionStatus.Queued.ToString() });
    }

    [HttpPost("test-mapping")]
    public async Task<IActionResult> TestMapping([FromBody] TestMappingRequestDto request)
    {
        try
        {
            string result;
            if (request.StepType == "Branch")
            {
                var branchResult = await _codeExecutionService.ExecuteBranchAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson);
                result = branchResult.ToString().ToLower();
            }
            else if (request.TargetProperty == "PostFlightCode")
            {
                result = await _codeExecutionService.ExecutePostFlightAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson, request.HttpResponseJson);
            }
            else if (request.TargetProperty == "UrlCode")
            {
                result = await _codeExecutionService.ExecuteUrlAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson);
            }
            else
            {
                result = await _codeExecutionService.ExecuteMappingAsync(request.MappingCode, request.FlowStateJson, request.PersistedStateJson);
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
        var persistedStateJson = await _repository.GetPersistedStateAsync(flowId);
        return Ok(new PersistedStateDto { FlowId = flowId, PersistedStateJson = persistedStateJson });
    }

    [HttpPut("{flowId}/persisted-state")]
    public async Task<IActionResult> UpdatePersistedState([FromRoute] Guid flowId, [FromBody] PersistedStateDto dto)
    {
        await _repository.UpdatePersistedStateAsync(flowId, dto.PersistedStateJson);
        return Ok(new PersistedStateDto { FlowId = flowId, PersistedStateJson = dto.PersistedStateJson });
    }

    [HttpDelete("{flowId}/persisted-state")]
    public async Task<IActionResult> ResetPersistedState([FromRoute] Guid flowId)
    {
        await _repository.UpdatePersistedStateAsync(flowId, "{}");
        return NoContent();
    }

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
