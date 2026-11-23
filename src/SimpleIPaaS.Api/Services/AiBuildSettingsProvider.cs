using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Services;

public sealed record AiBuildLimits(
    int MaxToolCalls,
    int MaxChatRounds,
    int MaxDraftRepairs,
    TimeSpan MaxBuildDuration,
    int MaxContextChars,
    int MaxNodes,
    int MaxEdges,
    int MaxHistoryTurns,
    int MaxHistoryCharsPerTurn,
    int MaxPromptChars,
    int MaxFlowPromptChars,
    int MaxOutputTokens)
{
    public static AiBuildLimits Default { get; } = new(
        MaxToolCalls: 48,
        MaxChatRounds: 60,
        MaxDraftRepairs: 4,
        MaxBuildDuration: TimeSpan.FromMinutes(30),
        MaxContextChars: 100_000,
        MaxNodes: 100,
        MaxEdges: 250,
        MaxHistoryTurns: 12,
        MaxHistoryCharsPerTurn: 4_000,
        MaxPromptChars: 48_000,
        MaxFlowPromptChars: 72_000,
        MaxOutputTokens: 32_768);

    public static AiBuildLimits From(AiBuildSettings? settings) => settings == null
        ? Default
        : new(
            settings.MaxToolCalls,
            settings.MaxChatRounds,
            settings.MaxDraftRepairs,
            TimeSpan.FromMinutes(settings.MaxBuildMinutes),
            settings.MaxContextChars,
            settings.MaxNodes,
            settings.MaxEdges,
            settings.MaxHistoryTurns,
            settings.MaxHistoryCharsPerTurn,
            settings.MaxPromptChars,
            settings.MaxFlowPromptChars,
            settings.MaxOutputTokens);
}

public sealed class AiBuildSettingsProvider
{
    private readonly IPaaSContext _db;

    public AiBuildSettingsProvider(IPaaSContext db)
    {
        _db = db;
    }

    public async Task<AiBuildLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _db.AiBuildSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == 1, cancellationToken);
        return AiBuildLimits.From(settings);
    }

    public async Task<AiBuildSettingsDto> GetDtoAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _db.AiBuildSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings == null) return new AiBuildSettingsDto();
        return ToDto(settings);
    }

    public async Task<AiBuildSettingsDto> SaveAsync(AiBuildSettingsDto dto, CancellationToken cancellationToken = default)
    {
        var settings = await _db.AiBuildSettings.FirstOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings == null)
        {
            settings = new AiBuildSettings { Id = 1 };
            _db.AiBuildSettings.Add(settings);
        }

        settings.MaxToolCalls = dto.MaxToolCalls;
        settings.MaxChatRounds = dto.MaxChatRounds;
        settings.MaxDraftRepairs = dto.MaxDraftRepairs;
        settings.MaxBuildMinutes = dto.MaxBuildMinutes;
        settings.MaxContextChars = dto.MaxContextChars;
        settings.MaxNodes = dto.MaxNodes;
        settings.MaxEdges = dto.MaxEdges;
        settings.MaxHistoryTurns = dto.MaxHistoryTurns;
        settings.MaxHistoryCharsPerTurn = dto.MaxHistoryCharsPerTurn;
        settings.MaxPromptChars = dto.MaxPromptChars;
        settings.MaxFlowPromptChars = dto.MaxFlowPromptChars;
        settings.MaxOutputTokens = dto.MaxOutputTokens;
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(settings);
    }

    private static AiBuildSettingsDto ToDto(AiBuildSettings settings) => new()
    {
        MaxToolCalls = settings.MaxToolCalls,
        MaxChatRounds = settings.MaxChatRounds,
        MaxDraftRepairs = settings.MaxDraftRepairs,
        MaxBuildMinutes = settings.MaxBuildMinutes,
        MaxContextChars = settings.MaxContextChars,
        MaxNodes = settings.MaxNodes,
        MaxEdges = settings.MaxEdges,
        MaxHistoryTurns = settings.MaxHistoryTurns,
        MaxHistoryCharsPerTurn = settings.MaxHistoryCharsPerTurn,
        MaxPromptChars = settings.MaxPromptChars,
        MaxFlowPromptChars = settings.MaxFlowPromptChars,
        MaxOutputTokens = settings.MaxOutputTokens,
        UpdatedAt = settings.UpdatedAt
    };
}
