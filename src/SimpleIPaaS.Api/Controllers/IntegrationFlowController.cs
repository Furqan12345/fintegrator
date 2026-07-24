using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
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
    private readonly IIntegrationRepository _repository;
    private readonly IAdvancedCodeExecutionService _codeExecutionService;
    private readonly FlowRunService _flowRunService;
    private readonly ITenantContext _tenantContext;

    public IntegrationFlowController(
        IIntegrationRepository repository,
        IAdvancedCodeExecutionService codeExecutionService,
        FlowRunService flowRunService,
        ITenantContext tenantContext)
    {
        _repository = repository;
        _codeExecutionService = codeExecutionService;
        _flowRunService = flowRunService;
        _tenantContext = tenantContext;
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
        EnsureWebhookSecret(flow);
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
        EnsureWebhookSecret(flow);
        await _repository.UpdateAsync(flow);

        return Ok(flow.ToDto());
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

    private IActionResult FlowValidationProblem(List<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError("flow", error);
        }

        return ValidationProblem(ModelState);
    }

    private static void EnsureWebhookSecret(Domain.Entities.IntegrationFlow flow)
    {
        if (flow.TriggerType == TriggerType.Webhook && string.IsNullOrWhiteSpace(flow.WebhookSecret))
        {
            flow.WebhookSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }
    }
}
