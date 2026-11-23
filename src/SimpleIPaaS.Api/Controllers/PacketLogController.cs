using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/packetlogs")]
public sealed class PacketLogController : ControllerBase
{
    private readonly IExecutionRepository _executions;

    public PacketLogController(IExecutionRepository executions)
    {
        _executions = executions;
    }

    [HttpGet("")]
    public async Task<ActionResult<List<PacketLogDto>>> GetPage(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? flowId = null,
        [FromQuery] Guid? flowExecutionId = null,
        [FromQuery] Guid? integrationId = null,
        [FromQuery] Guid? connectionId = null,
        [FromQuery] string? search = null,
        [FromQuery] string? outcome = null,
        [FromQuery] string? method = null,
        [FromQuery] int? hours = null,
        [FromQuery] bool manualOnly = false)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 200) pageSize = 50;

        var items = await _executions.GetPacketLogPageAsync(new PacketLogPageFilter(
            flowId, flowExecutionId, integrationId, connectionId, search?.Trim(), outcome, method?.Trim(), hours, page, pageSize, manualOnly));
        return Ok(items.Select(item => new PacketLogDto
        {
            PacketId = item.PacketId,
            FlowExecutionId = item.FlowExecutionId,
            StartedAt = item.StartedAt,
            FlowName = item.FlowName,
            IntegrationName = item.IntegrationName,
            NodeName = item.NodeName,
            ConnectionName = item.ConnectionName ?? "Manual / node config",
            HttpMethod = item.HttpMethod,
            RequestUrl = item.RequestUrl,
            StatusCode = item.StatusCode,
            DurationMs = item.DurationMs,
            PageNumber = item.PageNumber,
            Attempt = item.Attempt,
            Error = item.Error
        }).ToList());
    }

    [HttpGet("{packetId:guid}")]
    public async Task<ActionResult<PacketLogDetailDto>> GetDetail(Guid packetId)
    {
        var item = await _executions.GetPacketLogDetailAsync(packetId);
        if (item == null) return NotFound();
        return Ok(new PacketLogDetailDto
        {
            PacketId = item.PacketId,
            FlowExecutionId = item.FlowExecutionId,
            StartedAt = item.StartedAt,
            CompletedAt = item.CompletedAt,
            DurationMs = item.DurationMs,
            FlowName = item.FlowName,
            IntegrationName = item.IntegrationName,
            NodeName = item.NodeName,
            ConnectionName = item.ConnectionName ?? "Manual / node config",
            HttpMethod = item.HttpMethod,
            RequestUrl = item.RequestUrl,
            StatusCode = item.StatusCode,
            PageNumber = item.PageNumber,
            Attempt = item.Attempt,
            Error = item.Error,
            RequestHeadersJson = item.RequestHeadersJson,
            RequestBody = item.RequestBody,
            ResponseHeadersJson = item.ResponseHeadersJson,
            ResponseBody = item.ResponseBody
        });
    }
}
