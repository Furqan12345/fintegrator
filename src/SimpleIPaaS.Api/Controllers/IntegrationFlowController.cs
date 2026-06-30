using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
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
        var dtos = flows.Select(flow => new IntegrationFlowDto
        {
            Id = flow.Id,
            Name = flow.Name,
            Description = flow.Description,
            Nodes = flow.Nodes.Select(n => new IntegrationStepDto
            {
                Id = n.Id,
                StepType = n.StepType.ToString(),
                PositionX = n.PositionX,
                PositionY = n.PositionY,
                EndpointUrl = n.EndpointUrl,
                HttpMethod = n.HttpMethod,
                AuthType = n.AuthType.ToString(),
                AuthToken = n.AuthToken,
                AuthUsername = n.AuthUsername,
                AuthPassword = n.AuthPassword,
                MappingCode = n.MappingCode,
                ConnectionId = n.ConnectionId,
                NodeName = n.NodeName,
                UrlCode = n.UrlCode,
                PreFlightCode = n.PreFlightCode,
                PostFlightCode = n.PostFlightCode
            }).ToList(),
            Edges = flow.Edges.Select(e => new IntegrationEdgeDto
            {
                Id = e.Id,
                SourceNodeId = e.SourceNodeId,
                TargetNodeId = e.TargetNodeId,
                SourcePortId = e.SourcePortId,
                TargetPortId = e.TargetPortId
            }).ToList()
        }).ToArray();

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var flow = await _repository.GetByIdAsync(id);
        if (flow == null) return NotFound();

        var dto = new IntegrationFlowDto
        {
            Id = flow.Id,
            Name = flow.Name,
            Description = flow.Description,
            Nodes = flow.Nodes.Select(n => new IntegrationStepDto
            {
                Id = n.Id,
                StepType = n.StepType.ToString(),
                PositionX = n.PositionX,
                PositionY = n.PositionY,
                EndpointUrl = n.EndpointUrl,
                HttpMethod = n.HttpMethod,
                AuthType = n.AuthType.ToString(),
                AuthToken = n.AuthToken,
                AuthUsername = n.AuthUsername,
                AuthPassword = n.AuthPassword,
                MappingCode = n.MappingCode,
                ConnectionId = n.ConnectionId,
                NodeName = n.NodeName,
                UrlCode = n.UrlCode,
                PreFlightCode = n.PreFlightCode,
                PostFlightCode = n.PostFlightCode
            }).ToList(),
            Edges = flow.Edges.Select(e => new IntegrationEdgeDto
            {
                Id = e.Id,
                SourceNodeId = e.SourceNodeId,
                TargetNodeId = e.TargetNodeId,
                SourcePortId = e.SourcePortId,
                TargetPortId = e.TargetPortId
            }).ToList()
        };

        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] IntegrationFlowDto dto)
    {
        var flow = MapFromDto(dto);
        flow.Id = Guid.NewGuid();
        await _repository.AddAsync(flow);
        dto.Id = flow.Id;
        return CreatedAtAction(nameof(Get), new { id = flow.Id }, dto);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] IntegrationFlowDto dto)
    {
        if (id != dto.Id) return BadRequest("ID mismatch");

        await _repository.DeleteAsync(id);
        var flow = MapFromDto(dto);
        await _repository.AddAsync(flow);
        
        return NoContent();
    }

    [HttpPost("{id}/run")]
    public async Task<IActionResult> Run(Guid id)
    {
        try
        {
            var result = await _runner.ExecuteFlowAsync(id);
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
                var branchResult = await _codeExecutionService.ExecuteBranchAsync(request.MappingCode, request.FlowStateJson);
                result = branchResult.ToString().ToLower();
            }
            else if (request.TargetProperty == "PostFlightCode")
            {
                result = await _codeExecutionService.ExecutePostFlightAsync(request.MappingCode, request.FlowStateJson, request.HttpResponseJson);
            }
            else if (request.TargetProperty == "UrlCode")
            {
                result = await _codeExecutionService.ExecuteUrlAsync(request.MappingCode, request.FlowStateJson);
            }
            else
            {
                result = await _codeExecutionService.ExecuteMappingAsync(request.MappingCode, request.FlowStateJson);
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

    private IntegrationFlow MapFromDto(IntegrationFlowDto dto)
    {
        return new IntegrationFlow
        {
            Id = dto.Id,
            Name = dto.Name,
            Description = dto.Description,
            Nodes = dto.Nodes.Select(n => new IntegrationStep
            {
                Id = n.Id,
                StepType = Enum.Parse<StepType>(n.StepType),
                PositionX = n.PositionX,
                PositionY = n.PositionY,
                EndpointUrl = n.EndpointUrl,
                HttpMethod = n.HttpMethod,
                AuthType = Enum.Parse<AuthType>(n.AuthType),
                AuthToken = n.AuthToken,
                AuthUsername = n.AuthUsername,
                AuthPassword = n.AuthPassword,
                MappingCode = n.MappingCode,
                ConnectionId = n.ConnectionId,
                NodeName = n.NodeName,
                UrlCode = n.UrlCode,
                PreFlightCode = n.PreFlightCode,
                PostFlightCode = n.PostFlightCode
            }).ToList(),
            Edges = dto.Edges.Select(e => new IntegrationEdge
            {
                Id = e.Id,
                SourceNodeId = e.SourceNodeId,
                TargetNodeId = e.TargetNodeId,
                SourcePortId = e.SourcePortId,
                TargetPortId = e.TargetPortId
            }).ToList()
        };
    }
}
