using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/ai")]
public sealed class AiFlowBuilderController : ControllerBase
{
    private readonly IPaaSContext _db;
    private readonly IEncryptionService _encryption;
    private readonly AiFlowBuilderService _builder;

    public AiFlowBuilderController(IPaaSContext db, IEncryptionService encryption, AiFlowBuilderService builder)
    {
        _db = db;
        _encryption = encryption;
        _builder = builder;
    }

    [HttpGet("providers")]
    public async Task<ActionResult<AiProviderSettingsDto[]>> GetProviders()
    {
        var providers = await _db.AiProviderSettings.AsNoTracking()
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.Name)
            .ToArrayAsync();
        return Ok(providers.Select(ToDto).ToArray());
    }

    [HttpGet("providers/{id:guid}")]
    public async Task<ActionResult<AiProviderSettingsDto>> GetProviderById(Guid id)
    {
        var provider = await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == id);
        return provider == null ? NotFound() : Ok(ToDto(provider));
    }

    [HttpPost("providers")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> CreateProvider([FromBody] AiProviderSettingsUpdateDto request)
    {
        var validation = ValidateProvider(request, requireApiKey: true);
        if (validation != null) return BadRequest(new { message = validation });

        var name = request.Name.Trim();
        if (await NameExistsAsync(name, null))
            return Conflict(new { message = "A provider with this name already exists." });

        var existing = await _db.AiProviderSettings.ToListAsync();
        var makeDefault = request.IsDefault || existing.Count == 0;
        if (makeDefault)
        {
            foreach (var provider in existing) provider.IsDefault = false;
        }

        var created = new AiProviderSettings
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsDefault = makeDefault,
            ProviderType = NormalizeProviderType(request.ProviderType),
            // Store the API root, not the URL the operator pasted: the mode decides whether the
            // app appends /chat/completions or /responses at request time.
            Endpoint = AiProviderEndpoints.NormalizeRoot(request.Endpoint),
            ApiMode = string.IsNullOrWhiteSpace(request.ApiMode)
                ? AiApiModes.DefaultFor(NormalizeProviderType(request.ProviderType), request.Endpoint)
                : AiApiModes.Normalize(request.ApiMode),
            ReasoningEffort = AiReasoningEfforts.Normalize(request.ReasoningEffort),
            EnableWebSearch = request.EnableWebSearch ?? false,
            Model = request.Model.Trim(),
            EncryptedApiKey = await _encryption.EncryptAsync(request.ApiKey.Trim()),
            UpdatedAt = DateTime.UtcNow
        };
        _db.AiProviderSettings.Add(created);
        try
        {
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "A provider with this name already exists." });
        }

        return CreatedAtAction(nameof(GetProviderById), new { id = created.Id }, ToDto(created));
    }

    [HttpPut("providers/{id:guid}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> UpdateProvider(Guid id, [FromBody] AiProviderSettingsUpdateDto request)
    {
        var provider = await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == id);
        if (provider == null) return NotFound();

        var validation = ValidateProvider(request, requireApiKey: false);
        if (validation != null) return BadRequest(new { message = validation });

        var name = request.Name.Trim();
        if (await NameExistsAsync(name, id))
            return Conflict(new { message = "A provider with this name already exists." });

        provider.Name = name;
        provider.ProviderType = NormalizeProviderType(request.ProviderType);
        provider.Endpoint = AiProviderEndpoints.NormalizeRoot(request.Endpoint);
        // A blank mode on update keeps the stored one (an old client, or the compatibility
        // route, must never flip an existing provider's API — or the operator's choice).
        if (!string.IsNullOrWhiteSpace(request.ApiMode)) provider.ApiMode = AiApiModes.Normalize(request.ApiMode);
        provider.ReasoningEffort = AiReasoningEfforts.Normalize(request.ReasoningEffort);
        // Null means "not sent" (an older client), which keeps the stored flag.
        if (request.EnableWebSearch.HasValue) provider.EnableWebSearch = request.EnableWebSearch.Value;
        provider.Model = request.Model.Trim();
        if (request.ClearApiKey) provider.EncryptedApiKey = string.Empty;
        else if (!string.IsNullOrWhiteSpace(request.ApiKey))
            provider.EncryptedApiKey = await _encryption.EncryptAsync(request.ApiKey.Trim());

        if (request.IsDefault)
            await MakeDefaultAsync(provider.Id);
        else if (!provider.IsDefault && !await _db.AiProviderSettings.AnyAsync(item => item.IsDefault))
            provider.IsDefault = true;

        provider.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "A provider with this name already exists." });
        }

        return Ok(ToDto(provider));
    }

    [HttpPost("providers/{id:guid}/default")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> SetDefaultProvider(Guid id)
    {
        var provider = await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == id);
        if (provider == null) return NotFound();
        await MakeDefaultAsync(id);
        provider.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(ToDto(provider));
    }

    [HttpDelete("providers/{id:guid}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> DeleteProvider(Guid id)
    {
        var provider = await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == id);
        if (provider == null) return NotFound();

        var wasDefault = provider.IsDefault;
        _db.AiProviderSettings.Remove(provider);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        var remaining = await _db.AiProviderSettings.OrderBy(item => item.Name).ToListAsync();
        if (remaining.Count > 0 && (wasDefault || !remaining.Any(item => item.IsDefault)))
        {
            foreach (var item in remaining) item.IsDefault = item.Id == remaining[0].Id;
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
        }
        return NoContent();
    }

    [HttpPost("providers/{id:guid}/test")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> TestProviderById(Guid id)
    {
        var provider = await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == id);
        if (provider == null) return NotFound();
        return await TestProviderAsync(provider, HttpContext.RequestAborted);
    }

    // Compatibility routes for clients from the original single-provider builder.
    [HttpGet("provider")]
    public async Task<ActionResult<AiProviderSettingsDto>> GetProvider()
    {
        var provider = await GetDefaultProviderAsync();
        return Ok(provider == null ? new AiProviderSettingsDto() : ToDto(provider));
    }

    [HttpPut("provider")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> SaveProvider([FromBody] AiProviderSettingsUpdateDto request)
    {
        var provider = await GetDefaultProviderAsync();
        request.Name = string.IsNullOrWhiteSpace(request.Name) ? provider?.Name ?? "Default provider" : request.Name;
        request.IsDefault = true;
        return provider == null
            ? await CreateProvider(request)
            : await UpdateProvider(provider.Id, request);
    }

    [HttpPost("provider/test")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> TestDefaultProvider()
    {
        var provider = await GetDefaultProviderAsync();
        if (provider == null || string.IsNullOrWhiteSpace(provider.EncryptedApiKey))
            return BadRequest(new { message = "Save an AI provider and API key first." });
        return await TestProviderAsync(provider, HttpContext.RequestAborted);
    }

    [HttpPost("flow/jobs")]
    [RequestSizeLimit(2_000_000)]
    public async Task<ActionResult<AiBuildJobDto>> CreateBuildJob([FromBody] AiBuildJobCreateDto request)
    {
        var provider = request.ProviderId.HasValue
            ? await _db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == request.ProviderId.Value)
            : await GetDefaultProviderAsync();
        if (provider == null)
            return BadRequest(new { message = "Select an AI provider before generating a flow." });
        if (string.IsNullOrWhiteSpace(provider.EncryptedApiKey))
            return BadRequest(new { message = "This AI provider has no saved API key. Edit it in AI Providers first." });

        var instruction = (request.Instruction ?? string.Empty).Trim();
        if (instruction.Length == 0) return BadRequest(new { message = "Describe the integration you want to build." });
        if (instruction.Length > 32_000) return BadRequest(new { message = "Keep the request under 32000 characters." });

        var job = new AiBuildJob
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Model = provider.Model,
            Instruction = instruction,
            CurrentFlowJson = SerializeCurrentFlow(request.CurrentFlow),
            Status = AiBuildJobStatus.Queued,
            ProgressNote = "Waiting in the build queue",
            CreatedAt = DateTime.UtcNow
        };
        _db.AiBuildJobs.Add(job);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return AcceptedAtAction(nameof(GetBuildJob), new { id = job.Id }, ToJobDto(job));
    }

    [HttpGet("flow/jobs")]
    public async Task<ActionResult<List<AiBuildJobDto>>> GetBuildJobs()
    {
        var jobs = await _db.AiBuildJobs.AsNoTracking()
            .OrderByDescending(job => job.CreatedAt)
            .Take(20)
            .ToListAsync();
        return Ok(jobs.Select(job => ToJobDto(job, includeFlow: false)).ToList());
    }

    [HttpGet("flow/jobs/{id:guid}")]
    public async Task<ActionResult<AiBuildJobDto>> GetBuildJob(Guid id)
    {
        var job = await _db.AiBuildJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        return job == null ? NotFound() : Ok(ToJobDto(job));
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

    private static AiBuildJobDto ToJobDto(AiBuildJob job, bool includeFlow = true) => new()
    {
        Id = job.Id,
        ProviderId = job.ProviderId,
        ProviderName = job.ProviderName,
        Model = job.Model,
        Instruction = job.Instruction,
        Status = job.Status,
        ProgressNote = job.ProgressNote,
        ToolCallsUsed = job.ToolCallsUsed,
        Warnings = ParseList(job.WarningsJson),
        Sources = ParseList(job.SourcesJson),
        Flow = includeFlow ? ParseFlow(job.ResultFlowJson) : null,
        Error = job.Error,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt
    };

    private static List<string> ParseList(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }

    private static IntegrationFlowDto? ParseFlow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<IntegrationFlowDto>(json); }
        catch (JsonException) { return null; }
    }

    private async Task<IActionResult> TestProviderAsync(AiProviderSettings provider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.EncryptedApiKey))
            return BadRequest(new { message = "This AI provider has no saved API key." });
        try
        {
            var key = await _encryption.DecryptAsync(provider.EncryptedApiKey);
            return Ok(await _builder.TestConnectionAsync(provider.ProviderType, provider.Endpoint, provider.Model, key, cancellationToken,
                AiProviderRequestOptions.From(provider)));
        }
        catch (AiFlowBuilderException ex) { return BadRequest(new { message = ex.Message }); }
        catch (ProviderRequestException ex) { return ProviderError(ex.StatusCode, ex.ProviderMessage); }
        catch (HttpRequestException) { return StatusCode(502, new { message = "Could not reach the configured AI provider." }); }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(504, new { message = "The AI provider request timed out. Try again." }); }
        catch (System.Security.Cryptography.CryptographicException) { return StoredCredentialUnreadable(); }
        catch (FormatException) { return StoredCredentialUnreadable(); }
    }

    // A rotated Encryption:Key makes every stored ciphertext undecryptable. That is an operator
    // fix (re-enter the secret), not a server fault, so it must not surface as a 500.
    private ObjectResult StoredCredentialUnreadable() => StatusCode(409, new
    {
        message = ProviderErrorMessages.StoredCredentialUnreadable
    });

    private async Task<AiProviderSettings?> GetDefaultProviderAsync() =>
        await _db.AiProviderSettings
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.Name)
            .FirstOrDefaultAsync();

    private async Task<bool> NameExistsAsync(string name, Guid? exceptId)
    {
        var query = _db.AiProviderSettings.AsQueryable();
        if (exceptId.HasValue) query = query.Where(provider => provider.Id != exceptId.Value);
        var names = await query.Select(provider => provider.Name).ToListAsync();
        return names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task MakeDefaultAsync(Guid id)
    {
        var providers = await _db.AiProviderSettings.ToListAsync();
        foreach (var provider in providers) provider.IsDefault = provider.Id == id;
    }

    private static string? ValidateProvider(AiProviderSettingsUpdateDto request, bool requireApiKey)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            return "Enter a provider name of at most 120 characters.";
        if (string.IsNullOrWhiteSpace(request.ProviderType) ||
            !(request.ProviderType.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase) ||
              request.ProviderType.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase)))
            return "Choose OpenAI-compatible or Azure OpenAI.";
        // The same shared rule the provider call path enforces (AiFlowBuilderService.ProviderEndpointRule):
        // an absolute http(s) API root or a full /chat/completions or /responses URL, no credentials
        // in the URL, no fragment. Saving an ftp:// or credential-bearing endpoint used to be accepted
        // here and only failed later at test time, with a message that never stated the rule.
        if (AiProviderEndpoints.Validate(request.Endpoint) is { } endpointRejection)
            return endpointRejection;
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 200)
            return "Enter a model or Azure deployment name.";
        if (request.ApiKey?.Length > 4000)
            return "The API key is too long.";
        if (requireApiKey && string.IsNullOrWhiteSpace(request.ApiKey))
            return "Enter an API key before saving this provider.";
        return null;
    }

    private static string NormalizeProviderType(string providerType) =>
        providerType.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase) ? "AzureOpenAI" : "OpenAICompatible";

    private static ActionResult ProviderError(HttpStatusCode status, string? providerMessage)
    {
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "The provider rejected the API key. Check the saved key.",
            HttpStatusCode.Forbidden => "The provider key does not have permission to use this model.",
            HttpStatusCode.NotFound => "The provider endpoint or model was not found. Check the endpoint and model name.",
            (HttpStatusCode)429 => "The provider rate limit or quota was reached. Try again later.",
            HttpStatusCode.BadRequest when !string.IsNullOrWhiteSpace(providerMessage) =>
                $"The provider rejected the request: {providerMessage}",
            _ => $"The AI provider returned HTTP {(int)status}. Check its endpoint and configuration."
        };
        if (status != HttpStatusCode.BadRequest && !string.IsNullOrWhiteSpace(providerMessage))
            message += " " + providerMessage;

        return new ObjectResult(new { message })
        {
            StatusCode = status == (HttpStatusCode)429 ? 429 : 502
        };
    }

    private static AiProviderSettingsDto ToDto(AiProviderSettings provider) => new()
    {
        Id = provider.Id,
        Name = provider.Name,
        IsDefault = provider.IsDefault,
        ProviderType = provider.ProviderType,
        ApiMode = AiApiModes.Normalize(provider.ApiMode),
        ReasoningEffort = AiReasoningEfforts.Normalize(provider.ReasoningEffort),
        EnableWebSearch = provider.EnableWebSearch,
        Endpoint = provider.Endpoint,
        Model = provider.Model,
        HasApiKey = !string.IsNullOrWhiteSpace(provider.EncryptedApiKey),
        UpdatedAt = provider.UpdatedAt
    };
}
