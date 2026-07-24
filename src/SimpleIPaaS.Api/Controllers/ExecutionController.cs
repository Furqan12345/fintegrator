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
[Route("api/executions")]
public class ExecutionController : ControllerBase
{
    private readonly IExecutionRepository _repository;
    private readonly ExecutionCancellationRegistry _cancellationRegistry;

    public ExecutionController(IExecutionRepository repository, ExecutionCancellationRegistry cancellationRegistry)
    {
        _repository = repository;
        _cancellationRegistry = cancellationRegistry;
    }

    // Kept for any existing callers
    [HttpGet("flow/{id}")]
    public async Task<IActionResult> GetFlowExecution(Guid id)
    {
        var execution = await _repository.GetFlowExecutionAsync(id);
        if (execution == null) return NotFound();
        return Ok(execution);
    }

    // Matches SimpleIPaaS.Client/Store/ExecutionEffects.cs
    [HttpGet("")]
    public async Task<IActionResult> GetExecutions(
        [FromQuery] Guid? flowId = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        ExecutionStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ExecutionStatus>(status, true, out var parsed))
            {
                ModelState.AddModelError(nameof(status), $"'{status}' is not a valid ExecutionStatus.");
                return ValidationProblem(ModelState);
            }

            statusFilter = parsed;
        }

        var executions = await _repository.GetFlowExecutionsAsync(flowId, statusFilter, page, pageSize);
        return Ok(executions.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetExecution(Guid id)
    {
        var execution = await _repository.GetFlowExecutionAsync(id);
        if (execution == null) return NotFound();
        return Ok(ToDto(execution));
    }

    // Matches SimpleIPaaS.Client/Store/ExecutionEffects.cs
    [HttpGet("{id}/steps")]
    public async Task<IActionResult> GetExecutionSteps(Guid id)
    {
        var steps = await _repository.GetStepExecutionsAsync(id);

        var dto = steps.Select(s => new StepExecutionDto
        {
            Id = s.Id,
            FlowExecutionId = s.FlowExecutionId,
            StepId = s.StepId,
            Status = s.Status.ToString(),
            StartedAt = s.StartedAt,
            CompletedAt = s.CompletedAt,
            HttpStatusCode = s.HttpStatusCode,
            ErrorMessage = s.ErrorMessage,
            RequestPayload = s.RequestPayload,
            ResponsePayload = s.ResponsePayload
        });

        return Ok(dto);
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var execution = await _repository.GetFlowExecutionAsync(id);
        if (execution == null) return NotFound();

        var signalled = _cancellationRegistry.Cancel(id);

        if (execution.Status == ExecutionStatus.Queued)
        {
            execution.Status = ExecutionStatus.Cancelled;
            execution.ErrorMessage = "Execution was cancelled.";
            execution.CompletedAt = DateTime.UtcNow;
            await _repository.UpdateFlowExecutionAsync(execution);
        }

        return Ok(new { success = true, signalled, status = execution.Status.ToString() });
    }

    private static FlowExecutionDto ToDto(FlowExecution e)
    {
        return new FlowExecutionDto
        {
            Id = e.Id,
            FlowId = e.FlowId,
            TenantId = e.TenantId,
            Status = e.Status.ToString(),
            StartedAt = e.StartedAt,
            CompletedAt = e.CompletedAt,
            ErrorMessage = e.ErrorMessage,
            TriggerSource = e.TriggerSource,
            TotalRecords = e.TotalRecords,
            SuccessRecords = e.SuccessRecords,
            FailedRecords = e.FailedRecords
        };
    }
}
