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

        var dto = new List<StepExecutionDto>();
        foreach (var step in steps)
        {
            var packets = await _repository.GetStepPacketLogsAsync(step.Id);
            dto.Add(new StepExecutionDto
            {
                Id = step.Id,
                FlowExecutionId = step.FlowExecutionId,
                StepId = step.StepId,
                NodeName = step.NodeName,
                Status = step.Status.ToString(),
                StartedAt = step.StartedAt,
                CompletedAt = step.CompletedAt,
                RecoveredAt = step.RecoveredAt,
                RecoveredByDeadLetterId = step.RecoveredByDeadLetterId,
                ReceivedInput = step.ReceivedInput,
                HttpStatusCode = step.HttpStatusCode,
                ErrorMessage = step.ErrorMessage,
                RequestPayload = step.RequestPayload,
                ResponsePayload = step.ResponsePayload,
                Packets = packets.Select(packet => new StepPacketLogDto
                {
                    Id = packet.Id,
                    Sequence = packet.Sequence,
                    Kind = packet.Kind,
                    PageNumber = packet.PageNumber,
                    Attempt = packet.Attempt,
                    HttpMethod = packet.HttpMethod,
                    RequestUrl = packet.RequestUrl,
                    StatusCode = packet.StatusCode,
                    RequestHeadersJson = packet.RequestHeadersJson,
                    RequestBody = packet.RequestBody,
                    ResponseHeadersJson = packet.ResponseHeadersJson,
                    ResponseBody = packet.ResponseBody,
                    StartedAt = packet.StartedAt,
                    CompletedAt = packet.CompletedAt,
                    DurationMs = packet.DurationMs,
                    Error = packet.Error
                }).ToList()
            });
        }

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
            FlowName = e.FlowName,
            IntegrationId = e.IntegrationId,
            IntegrationName = e.IntegrationName,
            Status = e.Status.ToString(),
            StartedAt = e.StartedAt,
            CompletedAt = e.CompletedAt,
            RecoveredAt = e.RecoveredAt,
            ErrorMessage = e.ErrorMessage,
            TriggerSource = e.TriggerSource,
            TotalRecords = e.TotalRecords,
            SuccessRecords = e.SuccessRecords,
            FailedRecords = e.FailedRecords
        };
    }
}
