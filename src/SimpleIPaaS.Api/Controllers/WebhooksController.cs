using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly IIntegrationRepository _repository;
    private readonly FlowRunService _flowRunService;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IIntegrationRepository repository,
        FlowRunService flowRunService,
        ITenantContext tenantContext,
        ILogger<WebhooksController> logger)
    {
        _repository = repository;
        _flowRunService = flowRunService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    [HttpPost("{flowId:guid}/{secret}")]
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

        _tenantContext.SetTenantId(flow.TenantId);
        var executionId = await _flowRunService.EnqueueAsync(flow.Id, flow.TenantId, "Webhook", payload, HttpContext.RequestAborted);

        _logger.LogInformation("Webhook trigger enqueued execution {ExecutionId} for flow {FlowId}", executionId, flowId);

        return Accepted(new { success = true, executionId, status = ExecutionStatus.Queued.ToString() });
    }

    private static bool SecretsMatch(string expected, string provided)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }
}
