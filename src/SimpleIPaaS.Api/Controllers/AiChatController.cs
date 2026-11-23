using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/ai/chat")]
public sealed class AiChatController : ControllerBase
{
    private readonly IPaaSContext _db;
    private readonly IIntegrationRepository _integrations;

    public AiChatController(IPaaSContext db, IIntegrationRepository integrations)
    {
        _db = db;
        _integrations = integrations;
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<List<AiChatSessionSummaryDto>>> GetSessions()
    {
        var sessions = await _db.AiChatSessions.AsNoTracking()
            .OrderByDescending(session => session.UpdatedAt)
            .Take(50)
            .ToListAsync();
        if (sessions.Count == 0) return Ok(new List<AiChatSessionSummaryDto>());

        var sessionIds = sessions.Select(session => session.Id).ToList();
        var messages = await _db.AiChatMessages.AsNoTracking()
            .Where(message => sessionIds.Contains(message.SessionId))
            .OrderByDescending(message => message.CreatedAt)
            .ToListAsync();

        return Ok(sessions.Select(session =>
        {
            var own = messages.Where(message => message.SessionId == session.Id).ToList();
            return new AiChatSessionSummaryDto
            {
                Id = session.Id,
                Title = session.Title,
                Status = session.Status,
                FlowId = session.FlowId,
                MessageCount = own.Count,
                LastMessagePreview = BuildPreview(own.OrderBy(message => message.CreatedAt).LastOrDefault()),
                UpdatedAt = session.UpdatedAt
            };
        }).ToList());
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<AiChatSessionDetailDto>> CreateSession([FromBody] AiChatSessionCreateDto request)
    {
        var session = new AiChatSession
        {
            FlowId = request.FlowId,
            LastProviderId = request.ProviderId,
            Status = AiChatSessionStatus.Active
        };
        _db.AiChatSessions.Add(session);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDetail(session, new List<AiChatMessage>()));
    }

    [HttpGet("sessions/{id:guid}")]
    public async Task<ActionResult<AiChatSessionDetailDto>> GetSession(Guid id)
    {
        var session = await _db.AiChatSessions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        if (session == null) return NotFound();
        var messages = await _db.AiChatMessages.AsNoTracking()
            .Where(message => message.SessionId == id)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Role == AiChatMessageRole.Agent)
            .ToListAsync();
        return Ok(ToDetail(session, messages));
    }

    [HttpGet("messages/{id:guid}")]
    public async Task<ActionResult<AiChatMessageDto>> GetMessage(Guid id)
    {
        var message = await _db.AiChatMessages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        return message == null ? NotFound() : Ok(ToMessage(message));
    }

    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> DeleteSession(Guid id)
    {
        var session = await _db.AiChatSessions.FirstOrDefaultAsync(item => item.Id == id);
        if (session == null) return NotFound();

        var busy = await _db.AiChatMessages.AnyAsync(message =>
            message.SessionId == id &&
            (message.Status == AiChatMessageStatus.Queued || message.Status == AiChatMessageStatus.Running));
        if (busy)
        {
            return Conflict(new { message = "Wait for the agent to finish, or end the session, before deleting it." });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(HttpContext.RequestAborted);
        await _db.AiChatMessages.Where(message => message.SessionId == id).ExecuteDeleteAsync();
        _db.AiChatSessions.Remove(session);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        await transaction.CommitAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("sessions/{id:guid}/messages")]
    [RequestSizeLimit(2_000_000)]
    public async Task<ActionResult<AiChatSessionDetailDto>> PostMessage(Guid id, [FromBody] AiChatMessageCreateDto request)
    {
        var session = await _db.AiChatSessions.FirstOrDefaultAsync(item => item.Id == id);
        if (session == null) return NotFound();
        if (session.Status == AiChatSessionStatus.Ended)
            return BadRequest(new { message = "This session has ended. Start a new session to keep building." });

        var busy = await _db.AiChatMessages.AnyAsync(message =>
            message.SessionId == id &&
            message.Role == AiChatMessageRole.Agent &&
            (message.Status == AiChatMessageStatus.Queued || message.Status == AiChatMessageStatus.Running));
        if (busy)
            return BadRequest(new { message = "The agent is still responding to the last prompt. Wait for it to finish." });

        var content = (request.Content ?? string.Empty).Trim();
        if (content.Length == 0) return BadRequest(new { message = "Describe what the agent should build or change." });
        if (content.Length > 32_000) return BadRequest(new { message = "Keep the prompt under 32000 characters." });

        var provider = await ResolveProviderAsync(request.ProviderId ?? session.LastProviderId);
        if (provider == null)
            return BadRequest(new { message = "Select an AI provider before building." });
        if (string.IsNullOrWhiteSpace(provider.EncryptedApiKey))
            return BadRequest(new { message = "This AI provider has no saved API key. Edit it in AI Providers first." });

        var now = DateTime.UtcNow;
        if (session.DraftFlowJson == null && request.CurrentFlow != null)
            session.DraftFlowJson = SerializeCurrentFlow(request.CurrentFlow);

        _db.AiChatMessages.Add(new AiChatMessage
        {
            SessionId = id,
            Role = AiChatMessageRole.User,
            Content = content,
            Status = AiChatMessageStatus.Complete,
            CreatedAt = now
        });
        _db.AiChatMessages.Add(new AiChatMessage
        {
            SessionId = id,
            Role = AiChatMessageRole.Agent,
            Instruction = content,
            Status = AiChatMessageStatus.Queued,
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Model = provider.Model,
            CreatedAt = now
        });

        if (session.Title == "New build session")
            session.Title = content.Length > 80 ? content[..80] : content;
        session.LastProviderId = provider.Id;
        session.UpdatedAt = now;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        var messages = await _db.AiChatMessages.AsNoTracking()
            .Where(message => message.SessionId == id)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Role == AiChatMessageRole.Agent)
            .ToListAsync();
        return Ok(ToDetail(session, messages));
    }

    // Called by the designer after a flow is saved. A session started for a brand-new flow has
    // no FlowId yet; binding it here makes the next AI turn load the saved flow (including
    // connection/script edits made outside the session) instead of its stale draft snapshot.
    [HttpPost("sessions/{id:guid}/flow")]
    public async Task<IActionResult> BindFlow(Guid id, [FromBody] AiChatBindFlowDto request)
    {
        var session = await _db.AiChatSessions.FirstOrDefaultAsync(item => item.Id == id);
        if (session == null) return NotFound();

        var flow = await _integrations.GetByIdAsync(request.FlowId);
        if (flow == null) return NotFound(new { message = "Flow not found." });

        session.FlowId = flow.Id;
        session.DraftFlowJson = SerializeCurrentFlow(flow.ToDto());
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpPost("sessions/{id:guid}/end")]
    public async Task<ActionResult<AiChatSessionDetailDto>> EndSession(Guid id)
    {
        var session = await _db.AiChatSessions.FirstOrDefaultAsync(item => item.Id == id);
        if (session == null) return NotFound();
        session.Status = AiChatSessionStatus.Ended;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        var messages = await _db.AiChatMessages.AsNoTracking()
            .Where(message => message.SessionId == id)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Role == AiChatMessageRole.Agent)
            .ToListAsync();
        return Ok(ToDetail(session, messages));
    }

    private async Task<AiProviderSettings?> ResolveProviderAsync(Guid? providerId)
    {
        if (providerId.HasValue)
        {
            var selected = await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == providerId.Value);
            if (selected != null) return selected;
        }
        return await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.IsDefault)
            ?? await _db.AiProviderSettings.FirstOrDefaultAsync();
    }

    private AiChatSessionDetailDto ToDetail(AiChatSession session, List<AiChatMessage> messages) => new()
    {
        Id = session.Id,
        Title = session.Title,
        Status = session.Status,
        FlowId = session.FlowId,
        DraftFlow = ParseFlow(session.DraftFlowJson),
        Messages = messages.Select(ToMessage).ToList(),
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt
    };

    private static AiChatMessageDto ToMessage(AiChatMessage message) => new()
    {
        Id = message.Id,
        SessionId = message.SessionId,
        Role = message.Role,
        Content = message.Content,
        Instruction = message.Instruction,
        Status = message.Status,
        Steps = ParseSteps(message.StepsJson),
        Warnings = ParseList(message.WarningsJson),
        Sources = ParseList(message.SourcesJson),
        Flow = ParseFlow(message.ResultFlowJson),
        Error = message.Error,
        ProviderName = message.ProviderName,
        Model = message.Model,
        CreatedAt = message.CreatedAt,
        CompletedAt = message.CompletedAt
    };

    private static string BuildPreview(AiChatMessage? message)
    {
        if (message == null) return string.Empty;
        var text = message.Role == AiChatMessageRole.User
            ? message.Content
            : message.Status == AiChatMessageStatus.Failed ? message.Error : message.Content;
        text = (text ?? string.Empty).Trim();
        return text.Length > 120 ? text[..120] : text;
    }

    private static string? SerializeCurrentFlow(IntegrationFlowDto? flow)
    {
        if (flow == null) return null;
        flow.WebhookSecret = string.Empty;
        foreach (var node in flow.Nodes)
        {
            node.AuthToken = string.Empty;
            node.AuthUsername = string.Empty;
            node.AuthPassword = string.Empty;
            node.AuthConfigJson = string.Empty;
        }
        return JsonSerializer.Serialize(flow);
    }

    private static List<string> ParseList(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }

    private static List<AiChatStepDto> ParseSteps(string? json)
    {
        try { return JsonSerializer.Deserialize<List<AiChatStepDto>>(json ?? "[]") ?? new List<AiChatStepDto>(); }
        catch (JsonException) { return new List<AiChatStepDto>(); }
    }

    private static IntegrationFlowDto? ParseFlow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<IntegrationFlowDto>(json); }
        catch (JsonException) { return null; }
    }
}
