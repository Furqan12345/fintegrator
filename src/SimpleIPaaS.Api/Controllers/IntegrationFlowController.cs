using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/integrationflow")]
public class IntegrationFlowController : ControllerBase
{
    private readonly IIntegrationRepository _repository;
    private readonly FlowExecutor _runner;
    private readonly IAdvancedCodeExecutionService _codeExecutionService;

    public IntegrationFlowController(
        IIntegrationRepository repository,
        FlowExecutor runner,
        IAdvancedCodeExecutionService codeExecutionService)
    {
        _repository = repository;
        _runner = runner;
        _codeExecutionService = codeExecutionService;
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
        var flow = dto.ToEntity();
        flow.Id = Guid.NewGuid();
        await _repository.AddAsync(flow);
        return CreatedAtAction(nameof(Get), new { id = flow.Id }, flow.ToDto());
    }

    [HttpPut("{flowId}")]
    public async Task<IActionResult> Update([FromRoute] Guid flowId, [FromBody] IntegrationFlowDto dto)
    {
        if (flowId != dto.Id) return BadRequest("ID mismatch");

        var flow = dto.ToEntity();
        await _repository.UpdateAsync(flow);
        
        return Ok(flow.ToDto());
    }

    [HttpPost("{flowId}/run")]
    public async Task<IActionResult> Run([FromRoute] Guid flowId)
    {
        try
        {
            var result = await _runner.ExecuteFlowAsync(flowId);
            return Ok(new { success = true, executionId = result.Id, status = result.Status.ToString() });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
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
}
