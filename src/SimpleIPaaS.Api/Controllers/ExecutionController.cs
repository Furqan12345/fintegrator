using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/executions")]
public class ExecutionController : ControllerBase
{
    private readonly IExecutionRepository _repository;
    private readonly ITenantContext _tenantContext;

    public ExecutionController(IExecutionRepository repository, ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
    }

    // Kept for any existing callers
    [HttpGet("flow/{id}")]
    public async Task<IActionResult> GetFlowExecution(Guid id)
    {
        var execution = await _repository.GetFlowExecutionAsync(id);
        if (execution == null) return NotFound();
        return Ok(execution);
    }

    // Dev helper to seed a minimal execution + step so the UI/step endpoint can be tested quickly
    // GET api/Execution/dev/seed
    [HttpGet("dev/seed")]
    public async Task<IActionResult> SeedExecution()
    {
        try
        {
            // Use explicit tenant id so we don't depend on DI lifetime/initialization.
            var tenantId = _tenantContext.TenantId;
            if (tenantId == Guid.Empty)
            {
                tenantId = Guid.NewGuid();
            }

            var flowExecution = new FlowExecution
            {
                FlowId = Guid.NewGuid(),
                TenantId = tenantId,
                Status = ExecutionStatus.InProgress,
                StartedAt = DateTime.UtcNow
            };

            await _repository.AddFlowExecutionAsync(flowExecution);

            var stepExecution = new StepExecution
            {
                FlowExecutionId = flowExecution.Id,
                StepId = Guid.NewGuid(),
                TenantId = tenantId,
                Status = ExecutionStatus.Success,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                HttpStatusCode = 200,
                ErrorMessage = null,
                RequestPayload = "{}",
                ResponsePayload = "{\"ok\":true}"
            };

            await _repository.AddStepExecutionAsync(stepExecution);

            // (Optional) update counters for better UI display
            flowExecution.TotalRecords = 1;
            flowExecution.SuccessRecords = 1;
            flowExecution.FailedRecords = 0;
            flowExecution.Status = ExecutionStatus.Success;
            flowExecution.CompletedAt = DateTime.UtcNow;

            await _repository.UpdateFlowExecutionAsync(flowExecution);

            return Ok(new { executionId = flowExecution.Id });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = ex.Message,
                detail = ex.ToString()
            });
        }
    }

    // Matches SimpleIPaaS.Client/Store/ExecutionEffects.cs
    [HttpGet("")]
    public async Task<IActionResult> GetExecutions()
    {
        var executions = await _repository.GetFlowExecutionsAsync();

        var dto = executions.Select(e => new FlowExecutionDto
        {
            Id = e.Id,
            FlowId = e.FlowId,
            TenantId = e.TenantId,
            Status = e.Status.ToString(),
            StartedAt = e.StartedAt,
            CompletedAt = e.CompletedAt,
            ErrorMessage = e.ErrorMessage,
            TotalRecords = e.TotalRecords,
            SuccessRecords = e.SuccessRecords,
            FailedRecords = e.FailedRecords
        });

        return Ok(dto);
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

    // Dead Letter Queue endpoint for ActivityLog
    [HttpGet("deadletters")]
    public async Task<IActionResult> GetDeadLetters()
    {
        var deadLetters = await _repository.GetAllDeadLettersAsync();

        var dto = deadLetters.Select(d => new DeadLetterEntryDto
        {
            Id = d.Id,
            FlowExecutionId = d.FlowExecutionId,
            StepId = d.StepId,
            Payload = d.Payload,
            ErrorMessage = d.ErrorMessage,
            RetryCount = d.RetryCount,
            CreatedAt = d.CreatedAt,
            LastRetriedAt = d.LastRetriedAt,
            Status = d.Status
        });

        return Ok(dto);
    }
}
