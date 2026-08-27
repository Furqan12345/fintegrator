using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/deadletters")]
public class DeadLetterController : ControllerBase
{
    private static readonly string[] ReplaySafeMethods = { "GET", "PUT", "DELETE" };

    private readonly IExecutionRepository _repository;
    private readonly IIntegrationRepository _integrationRepository;

    public DeadLetterController(IExecutionRepository repository, IIntegrationRepository integrationRepository)
    {
        _repository = repository;
        _integrationRepository = integrationRepository;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetAll([FromQuery] string? status = null)
    {
        var entries = await _repository.GetAllDeadLettersAsync(status);
        return Ok(entries.Select(ToDto));
    }

    // Replay requests are dispatched through the shared database: this endpoint flags the
    // entry (ReplayRequestedAt) and returns immediately; the Engine's dead-letter worker
    // performs the actual HTTP replay out-of-process.
    [HttpPost("{id}/retry")]
    public async Task<IActionResult> Retry(Guid id)
    {
        var entry = await _repository.GetDeadLetterAsync(id);
        if (entry == null) return NotFound();

        if (entry.Status != "Pending" && entry.Status != "Retrying")
        {
            return Problem(
                title: "Not replayable",
                detail: $"Only Pending or Retrying entries can be replayed; this entry is '{entry.Status}'.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var flowExecution = await _repository.GetFlowExecutionAsync(entry.FlowExecutionId);
        if (flowExecution == null)
        {
            return Problem(
                title: "Not replayable",
                detail: "The originating execution no longer exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var flow = await _integrationRepository.GetByIdAsync(flowExecution.FlowId);
        var step = flow?.Nodes.FirstOrDefault(n => n.Id == entry.StepId);
        if (step == null || step.StepType != StepType.HttpAction)
        {
            return Problem(
                title: "Not replayable",
                detail: "The failed step no longer exists or is not an HTTP action.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var replaySafe = ReplaySafeMethods.Contains(step.HttpMethod, StringComparer.OrdinalIgnoreCase);
        var postOptIn = string.Equals(step.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) && flow!.AllowPostReplay;
        if (!replaySafe && !postOptIn)
        {
            return Problem(
                title: "Replay not permitted",
                detail: "This flow does not permit replaying POST steps. Enable POST replay on the flow to retry this entry.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        entry.Status = "Retrying";
        entry.ReplayRequestedAt = DateTime.UtcNow;
        await _repository.UpdateDeadLetterEntryAsync(entry);

        return Accepted(ToDto(entry));
    }

    [HttpPost("{id}/discard")]
    public async Task<IActionResult> Discard(Guid id)
    {
        var entry = await _repository.GetDeadLetterAsync(id);
        if (entry == null) return NotFound();

        entry.Status = "Discarded";
        await _repository.UpdateDeadLetterEntryAsync(entry);
        return Ok(ToDto(entry));
    }

    private static DeadLetterDto ToDto(DeadLetterEntry entry)
    {
        return new DeadLetterDto
        {
            Id = entry.Id,
            FlowExecutionId = entry.FlowExecutionId,
            StepId = entry.StepId,
            FlowName = entry.FlowName,
            IntegrationName = entry.IntegrationName,
            NodeName = entry.NodeName,
            Payload = entry.Payload,
            ErrorMessage = entry.ErrorMessage,
            AttemptHistoryJson = entry.AttemptHistoryJson,
            RetryCount = entry.RetryCount,
            CreatedAt = entry.CreatedAt,
            LastRetriedAt = entry.LastRetriedAt,
            ResolvedAt = entry.ResolvedAt,
            Status = entry.Status
        };
    }
}
