using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/ai/settings")]
[Authorize(Policy = "Admin")]
public sealed class AiSettingsController : ControllerBase
{
    private readonly AiBuildSettingsProvider _settings;

    public AiSettingsController(AiBuildSettingsProvider settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public async Task<ActionResult<AiBuildSettingsDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _settings.GetDtoAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<AiBuildSettingsDto>> Save(
        [FromBody] AiBuildSettingsDto request, CancellationToken cancellationToken)
    {
        var error = Validate(request);
        if (error != null) return BadRequest(new { message = error });
        return Ok(await _settings.SaveAsync(request, cancellationToken));
    }

    [HttpPost("reset")]
    public async Task<ActionResult<AiBuildSettingsDto>> Reset(CancellationToken cancellationToken) =>
        Ok(await _settings.SaveAsync(new AiBuildSettingsDto(), cancellationToken));

    private static string? Validate(AiBuildSettingsDto settings)
    {
        if (settings.MaxToolCalls is < 8 or > 200) return "Max tool calls must be between 8 and 200.";
        if (settings.MaxChatRounds is < 10 or > 200) return "Max rounds must be between 10 and 200.";
        if (settings.MaxDraftRepairs is < 0 or > 10) return "Max draft repairs must be between 0 and 10.";
        if (settings.MaxBuildMinutes is < 5 or > 180) return "Build timeout must be between 5 and 180 minutes.";
        if (settings.MaxContextChars is < 10_000 or > 500_000) return "Context limit must be between 10,000 and 500,000 characters.";
        if (settings.MaxNodes is < 1 or > 250) return "Max nodes must be between 1 and 250.";
        if (settings.MaxEdges is < 0 or > 500) return "Max edges must be between 0 and 500.";
        if (settings.MaxHistoryTurns is < 1 or > 50) return "History turns must be between 1 and 50.";
        if (settings.MaxHistoryCharsPerTurn is < 500 or > 20_000) return "History characters per turn must be between 500 and 20,000.";
        if (settings.MaxPromptChars is < 5_000 or > 200_000) return "Prompt limit must be between 5,000 and 200,000 characters.";
        if (settings.MaxFlowPromptChars is < 5_000 or > 300_000) return "Flow prompt limit must be between 5,000 and 300,000 characters.";
        if (settings.MaxOutputTokens is < 1_024 or > 262_144) return "Output tokens must be between 1,024 and 262,144.";
        return null;
    }
}
