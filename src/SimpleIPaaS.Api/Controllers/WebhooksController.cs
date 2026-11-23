using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
[EnableRateLimiting("webhook")]
public class WebhooksController : ControllerBase
{
    private readonly IIntegrationRepository _repository;
    private readonly FlowRunService _flowRunService;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IIntegrationRepository repository,
        FlowRunService flowRunService,
        ILogger<WebhooksController> logger)
    {
        _repository = repository;
        _flowRunService = flowRunService;
        _logger = logger;
    }

    [HttpPost("{flowId:guid}/{secret}")]
    [RequestSizeLimit(1_000_000)]
    public async Task<IActionResult> Trigger(Guid flowId, string secret)
    {
        var flow = await _repository.GetFlowForTriggerAsync(flowId);
        if (flow == null ||
            flow.TriggerType != TriggerType.Webhook ||
            string.IsNullOrEmpty(flow.WebhookSecret) ||
            !SecretsMatch(flow.WebhookSecret, secret))
        {
            return NotFound();
        }

        string payload;
        using (var reader = new StreamReader(Request.Body))
        {
            payload = await reader.ReadToEndAsync(HttpContext.RequestAborted);
        }

        var executionId = await _flowRunService.EnqueueAsync(flow.Id, "Webhook", payload, HttpContext.RequestAborted);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["FlowId"] = flow.Id,
            ["FlowExecutionId"] = executionId
        }))
        {
            _logger.LogInformation("Webhook trigger enqueued execution {ExecutionId} for flow {FlowId}", executionId, flowId);
        }

        return Accepted(new { success = true, executionId, status = ExecutionStatus.Queued.ToString() });
    }

    private static bool SecretsMatch(string expected, string provided)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }
}
