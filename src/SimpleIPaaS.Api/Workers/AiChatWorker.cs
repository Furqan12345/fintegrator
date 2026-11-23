using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Workers;

// Runs chat turns off the request thread (DB-as-bus, same contract as AiBuildJobWorker).
// Progress callbacks are appended to the message's step list as they happen so the chat
// shows the agent thinking live, and the session draft is updated when the turn ends so
// follow-up prompts (and resumed sessions) continue from the exact current state.
public sealed class AiChatWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AiChatWorker> _logger;

    public AiChatWorker(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<AiChatWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedMessagesAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claim = await TryClaimNextMessageAsync(stoppingToken);
                if (claim == null)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
                await RunTurnAsync(claim.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI chat worker loop failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task FailInterruptedMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
        var interrupted = await db.AiChatMessages
            .Where(message => message.Role == AiChatMessageRole.Agent && message.Status == AiChatMessageStatus.Running)
            .ToListAsync(cancellationToken);
        foreach (var message in interrupted)
        {
            message.Status = AiChatMessageStatus.Failed;
            message.Error = "The agent was interrupted by a service restart. Send the prompt again.";
            message.CompletedAt = DateTime.UtcNow;
        }
        if (interrupted.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid?> TryClaimNextMessageAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
        var next = await db.AiChatMessages
            .Where(message => message.Role == AiChatMessageRole.Agent && message.Status == AiChatMessageStatus.Queued)
            .OrderBy(message => message.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (next == null) return null;

        next.Status = AiChatMessageStatus.Running;
        await db.SaveChangesAsync(cancellationToken);
        return next.Id;
    }

    private async Task RunTurnAsync(Guid messageId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();

        var message = await db.AiChatMessages.FirstOrDefaultAsync(item => item.Id == messageId, stoppingToken);
        if (message == null) return;
        var session = await db.AiChatSessions.FirstOrDefaultAsync(item => item.Id == message.SessionId, stoppingToken);
        if (session == null)
        {
            await FailAsync(db, message, "The session for this turn no longer exists.");
            return;
        }

        var provider = await FindProviderAsync(db, message, stoppingToken);
        if (provider == null || string.IsNullOrWhiteSpace(provider.EncryptedApiKey))
        {
            await FailAsync(db, message, "The AI provider for this turn no longer exists or has no key.");
            return;
        }

        _logger.LogInformation("AI chat turn {MessageId} started with {Model}.", message.Id, provider.Model);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.ApplicationStopping);
        try
        {
            var key = await scope.ServiceProvider.GetRequiredService<IEncryptionService>().DecryptAsync(provider.EncryptedApiKey);
            var builder = scope.ServiceProvider.GetRequiredService<AiFlowBuilderService>();
            var history = await LoadRecentTurnsAsync(db, session.Id, message.Instruction, linkedCts.Token);
            // The provider row decides the API shape for every call of this turn (Chat Completions
            // or Responses, plus reasoning effort / built-in web search).
            var providerOptions = AiProviderRequestOptions.From(provider);

            if (ChatIntentGate.IsCasual(message.Instruction))
            {
                history.Add(("user", message.Instruction));
                var reply = await builder.ChatAsync(provider.ProviderType, provider.Endpoint, provider.Model, key, history, linkedCts.Token, providerOptions);
                await CompleteConversationalAsync(db, message, session, reply);
                _logger.LogInformation("AI chat turn {MessageId} answered conversationally.", message.Id);
                return;
            }

            // The model itself decides whether the turn wants a draft (BUILD) or an
            // English answer (CHAT); the service still routes a JSON envelope to the
            // canvas even on CHAT turns.
            var allowProseReply = !await builder.IsFlowRequestAsync(provider.ProviderType, provider.Endpoint,
                provider.Model, key, history, message.Instruction, linkedCts.Token, providerOptions);

            var request = new AiFlowBuildRequestDto
            {
                ProviderId = provider.Id,
                Instruction = message.Instruction,
                CurrentFlow = await ResolveCurrentFlowAsync(db, scope, session, linkedCts.Token)
            };

            var result = await builder.BuildAsync(provider.ProviderType, provider.Endpoint, provider.Model, key,
                request, linkedCts.Token,
                async (note, toolCalls) =>
                {
                    var steps = ParseSteps(message.StepsJson);
                    steps.Add(new AiChatStepDto
                    {
                        At = DateTime.UtcNow,
                        Note = note.Length > 500 ? note[..500] : note,
                        ToolCalls = toolCalls
                    });
                    message.StepsJson = JsonSerializer.Serialize(steps);
                    await db.SaveChangesAsync(CancellationToken.None);
                },
                conversationHistory: history, allowProseReply: allowProseReply, providerOptions: providerOptions);

            if (result.IsConversational)
            {
                await CompleteConversationalAsync(db, message, session, result.Summary);
                _logger.LogInformation("AI chat turn {MessageId} answered conversationally.", message.Id);
                return;
            }

            message.Status = AiChatMessageStatus.Complete;
            message.Content = string.IsNullOrWhiteSpace(result.Summary) ? "Draft ready to review" : result.Summary;
            message.ResultFlowJson = JsonSerializer.Serialize(result.Flow);
            message.WarningsJson = JsonSerializer.Serialize(result.Warnings);
            message.SourcesJson = JsonSerializer.Serialize(result.Sources);
            message.CompletedAt = DateTime.UtcNow;
            session.DraftFlowJson = message.ResultFlowJson;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation("AI chat turn {MessageId} completed.", message.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await FailAsync(db, message, "The agent was stopped with the service. Send the prompt again.");
        }
        catch (AiFlowBuilderException ex)
        {
            await FailAsync(db, message, ex.Message);
        }
        catch (ProviderRequestException ex)
        {
            await FailAsync(db, message, ProviderErrorMessages.Describe(ex));
        }
        catch (HttpRequestException)
        {
            await FailAsync(db, message, "Could not reach the configured AI provider.");
        }
        catch (OperationCanceledException)
        {
            await FailAsync(db, message, "The AI provider request timed out. Try again with a narrower request.");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await FailAsync(db, message, ProviderErrorMessages.StoredCredentialUnreadable);
        }
        catch (FormatException)
        {
            await FailAsync(db, message, ProviderErrorMessages.StoredCredentialUnreadable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI chat turn {MessageId} failed unexpectedly.", message.Id);
            await FailAsync(db, message, "The turn failed unexpectedly. Check the Debug Logs console for details.");
        }
    }

    private static async Task CompleteConversationalAsync(IPaaSContext db, AiChatMessage message, AiChatSession session, string reply)
    {
        message.Status = AiChatMessageStatus.Complete;
        message.Content = string.IsNullOrWhiteSpace(reply) ? "Noted — tell me what flow you want to build or change." : reply;
        message.CompletedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // Prior completed turns of the session (roles mapped for the provider), WITHOUT the
    // current instruction — callers append it themselves. The trailing user row that
    // mirrors the instruction is dropped so the instruction is never sent twice.
    private static async Task<List<(string Role, string Content)>> LoadRecentTurnsAsync(IPaaSContext db, Guid sessionId, string instruction, CancellationToken cancellationToken)
    {
        var rows = await db.AiChatMessages
            .Where(item => item.SessionId == sessionId && item.Status == AiChatMessageStatus.Complete)
            .OrderByDescending(item => item.CreatedAt)
            .Take(12)
            .ToListAsync(cancellationToken);
        rows.Reverse();

        while (rows.Count > 0 && rows[^1].Role == AiChatMessageRole.User && rows[^1].Content == instruction)
            rows.RemoveAt(rows.Count - 1);

        var turns = new List<(string Role, string Content)>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Content)) continue;
            turns.Add((row.Role == AiChatMessageRole.Agent ? "assistant" : "user", row.Content));
        }
        return turns;
    }

    // The saved flow is the source of truth for flow-bound sessions whenever the user
    // changed it after the agent's last proposal — otherwise an agent edit silently
    // wipes the user's designer changes (URLs, credentials) that the session snapshot
    // never saw. The session draft is kept in sync so the UI panel shows what the
    // agent actually works from.
    private static async Task<IntegrationFlowDto?> ResolveCurrentFlowAsync(IPaaSContext db, IServiceScope scope,
        AiChatSession session, CancellationToken cancellationToken)
    {
        var draft = DeserializeFlow(session.DraftFlowJson);
        if (session.FlowId is not Guid flowId) return draft;

        var saved = await scope.ServiceProvider.GetRequiredService<IIntegrationRepository>().GetByIdAsync(flowId);
        if (saved == null) return draft;

        var lastDraftWrite = await db.AiChatMessages
            .Where(item => item.SessionId == session.Id
                && item.Role == AiChatMessageRole.Agent
                && item.Status == AiChatMessageStatus.Complete
                && item.CompletedAt != null)
            .OrderByDescending(item => item.CompletedAt)
            .Select(item => item.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (saved.UpdatedAt <= (lastDraftWrite ?? DateTime.MinValue)) return draft;

        var savedDto = saved.ToDto();
        session.DraftFlowJson = JsonSerializer.Serialize(savedDto);
        return savedDto;
    }

    private static async Task<AiProviderSettings?> FindProviderAsync(IPaaSContext db, AiChatMessage message, CancellationToken cancellationToken)
    {
        if (message.ProviderId.HasValue)
            return await db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == message.ProviderId.Value, cancellationToken);
        return await db.AiProviderSettings.FirstOrDefaultAsync(item => item.IsDefault, cancellationToken)
            ?? await db.AiProviderSettings.FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task FailAsync(IPaaSContext db, AiChatMessage message, string error)
    {
        message.Status = AiChatMessageStatus.Failed;
        message.Error = error.Length > 1000 ? error[..1000] : error;
        message.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static List<AiChatStepDto> ParseSteps(string? json)
    {
        try { return JsonSerializer.Deserialize<List<AiChatStepDto>>(json ?? "[]", JsonOptions) ?? new List<AiChatStepDto>(); }
        catch (JsonException) { return new List<AiChatStepDto>(); }
    }

    private static IntegrationFlowDto? DeserializeFlow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<IntegrationFlowDto>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
