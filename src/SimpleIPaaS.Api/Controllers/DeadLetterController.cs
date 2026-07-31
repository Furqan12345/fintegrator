using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/deadletters")]
public class DeadLetterController : ControllerBase
{
    private readonly IExecutionRepository _repository;
    private readonly DeadLetterService _deadLetterService;

    public DeadLetterController(IExecutionRepository repository, DeadLetterService deadLetterService)
    {
        _repository = repository;
        _deadLetterService = deadLetterService;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetAll([FromQuery] string? status = null)
    {
        var entries = await _repository.GetAllDeadLettersAsync(status);
        return Ok(entries.Select(ToDto));
    }

    [HttpPost("{id}/retry")]
    public async Task<IActionResult> Retry(Guid id)
    {
        var result = await _deadLetterService.ReplayEntryAsync(id, force: true, HttpContext.RequestAborted);
        if (result.Outcome == DeadLetterReplayOutcome.NotFound || result.Entry == null) return NotFound();

        if (result.Outcome == DeadLetterReplayOutcome.PostReplayNotAllowed)
        {
            return Problem(
                title: "Replay not permitted",
                detail: "This flow does not permit replaying POST steps. Enable POST replay on the flow to retry this entry.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Ok(ToDto(result.Entry));
    }

    [HttpPost("{id}/discard")]
    public async Task<IActionResult> Discard(Guid id)
    {
        var entry = await _deadLetterService.DiscardEntryAsync(id);
        if (entry == null) return NotFound();
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
