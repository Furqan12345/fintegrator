using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/executions")]
public class ExecutionController : ControllerBase
{
    private readonly IExecutionRepository _repository;

    public ExecutionController(IExecutionRepository repository)
    {
        _repository = repository;
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
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? integrationId = null)
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

        var executions = await _repository.GetFlowExecutionsAsync(flowId, statusFilter, page, pageSize, integrationId);
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
        var steps = await _repository.GetStepExecutionSummariesAsync(id);
        var packets = await _repository.GetStepPacketLogSummariesAsync(id);

        var packetsByStep = packets
            .GroupBy(packet => packet.StepExecutionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(packet => packet.Sequence).ToList());

        var dto = steps.Select(step => new StepExecutionDto
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
            HttpStatusCode = step.HttpStatusCode,
            ErrorMessage = step.ErrorMessage,
            ReceivedInputSize = step.ReceivedInputSize,
            RequestPayloadSize = step.RequestPayloadSize,
            ResponsePayloadSize = step.ResponsePayloadSize,
            Packets = packetsByStep.TryGetValue(step.Id, out var stepPackets)
                ? stepPackets.Select(packet => new StepPacketLogDto
                {
                    Id = packet.Id,
                    Sequence = packet.Sequence,
                    Kind = packet.Kind,
                    PageNumber = packet.PageNumber,
                    Attempt = packet.Attempt,
                    HttpMethod = packet.HttpMethod,
                    RequestUrl = packet.RequestUrl,
                    StatusCode = packet.StatusCode,
                    StartedAt = packet.StartedAt,
                    CompletedAt = packet.CompletedAt,
                    DurationMs = packet.DurationMs,
                    Error = packet.Error,
                    RequestHeadersSize = packet.RequestHeadersSize,
                    RequestBodySize = packet.RequestBodySize,
                    ResponseHeadersSize = packet.ResponseHeadersSize,
                    ResponseBodySize = packet.ResponseBodySize
                }).ToList()
                : new List<StepPacketLogDto>()
        }).ToList();

        return Ok(dto);
    }

    [HttpGet("{id:guid}/steps/{stepId:guid}/payload")]
    public async Task<IActionResult> GetStepPayload(Guid id, Guid stepId)
    {
        var payload = await _repository.GetStepPayloadAsync(id, stepId);
        if (payload == null) return NotFound();

        return Ok(new StepPayloadDto
        {
            Id = payload.Id,
            ReceivedInput = payload.ReceivedInput,
            RequestPayload = payload.RequestPayload,
            ResponsePayload = payload.ResponsePayload
        });
    }

    [HttpGet("{id:guid}/packets/{packetId:guid}")]
    public async Task<IActionResult> GetPacketBody(Guid id, Guid packetId)
    {
        var packet = await _repository.GetStepPacketBodyAsync(id, packetId);
        if (packet == null) return NotFound();

        return Ok(new StepPacketBodyDto
        {
            Id = packet.Id,
            RequestHeadersJson = packet.RequestHeadersJson,
            RequestBody = packet.RequestBody,
            ResponseHeadersJson = packet.ResponseHeadersJson,
            ResponseBody = packet.ResponseBody
        });
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var execution = await _repository.GetFlowExecutionAsync(id);
        if (execution == null) return NotFound();

        // Cancellation is a database write: the Engine skips rows cancelled before they
        // started, and its cancellation watcher flips the running token when it observes
        // Status=Cancelled for an in-flight execution.
        var cancellable = execution.Status == ExecutionStatus.Queued || execution.Status == ExecutionStatus.InProgress;
        if (cancellable)
        {
            execution.Status = ExecutionStatus.Cancelled;
            execution.ErrorMessage = "Execution was cancelled.";
            execution.CompletedAt = DateTime.UtcNow;
            await _repository.UpdateFlowExecutionAsync(execution);
        }

        return Ok(new { success = cancellable, status = execution.Status.ToString() });
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
