using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Workers;

// Runs AI flow-build jobs off the request thread. A job is queued as a database
// row (DB-as-bus, same contract the Engine uses for runs) so the browser can
// navigate away while the agent researches, inspects data, and drafts.
public sealed class AiBuildJobWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AiBuildJobWorker> _logger;

    public AiBuildJobWorker(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<AiBuildJobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedJobsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claim = await TryClaimNextJobAsync(stoppingToken);
                if (claim == null)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }
                await RunJobAsync(claim.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI build worker loop failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task FailInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
        var interrupted = await db.AiBuildJobs
            .Where(job => job.Status == AiBuildJobStatus.Running)
            .ToListAsync(cancellationToken);
        foreach (var job in interrupted)
        {
            job.Status = AiBuildJobStatus.Failed;
            job.Error = "The build was interrupted by a service restart. Start a new build.";
            job.CompletedAt = DateTime.UtcNow;
        }
        if (interrupted.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid?> TryClaimNextJobAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
        var next = await db.AiBuildJobs
            .Where(job => job.Status == AiBuildJobStatus.Queued)
            .OrderBy(job => job.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (next == null) return null;

        next.Status = AiBuildJobStatus.Running;
        next.StartedAt = DateTime.UtcNow;
        next.ProgressNote = "Starting";
        await db.SaveChangesAsync(cancellationToken);
        return next.Id;
    }

    private async Task RunJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();

        var job = await db.AiBuildJobs.FirstOrDefaultAsync(item => item.Id == jobId, stoppingToken);
        if (job == null) return;
        var provider = await db.AiProviderSettings.FirstOrDefaultAsync(item => item.Id == job.ProviderId, stoppingToken);
        if (provider == null || string.IsNullOrWhiteSpace(provider.EncryptedApiKey))
        {
            await FailAsync(db, job, "The AI provider for this build no longer exists or has no key.");
            return;
        }

        _logger.LogInformation("AI build {JobId} started with {Model}.", job.Id, provider.Model);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.ApplicationStopping);
        try
        {
            var key = await scope.ServiceProvider.GetRequiredService<IEncryptionService>().DecryptAsync(provider.EncryptedApiKey);
            var builder = scope.ServiceProvider.GetRequiredService<AiFlowBuilderService>();
            var request = new AiFlowBuildRequestDto
            {
                ProviderId = provider.Id,
                Instruction = job.Instruction,
                CurrentFlow = DeserializeFlow(job.CurrentFlowJson)
            };

            var result = await builder.BuildAsync(provider.ProviderType, provider.Endpoint, provider.Model, key,
                request, linkedCts.Token,
                async (note, toolCalls) =>
                {
                    job.ProgressNote = note.Length > 500 ? note[..500] : note;
                    job.ToolCallsUsed = toolCalls;
                    await db.SaveChangesAsync(CancellationToken.None);
                },
                providerOptions: AiProviderRequestOptions.From(provider));

            if (result.IsConversational)
            {
                await FailAsync(db, job, "That message reads like chat, not a flow request. Describe the flow to build or change — or use the Build with AI chat for conversations.");
                return;
            }

            job.Status = AiBuildJobStatus.Completed;
            job.ProgressNote = "Draft ready to review";
            job.ResultFlowJson = JsonSerializer.Serialize(result.Flow);
            job.WarningsJson = JsonSerializer.Serialize(result.Warnings);
            job.SourcesJson = JsonSerializer.Serialize(result.Sources);
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation("AI build {JobId} completed.", job.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await FailAsync(db, job, "The build was stopped with the service. Start a new build.");
        }
        catch (AiFlowBuilderException ex)
        {
            await FailAsync(db, job, ex.Message);
        }
        catch (ProviderRequestException ex)
        {
            await FailAsync(db, job, ProviderErrorMessages.Describe(ex));
        }
        catch (HttpRequestException)
        {
            await FailAsync(db, job, "Could not reach the configured AI provider.");
        }
        catch (OperationCanceledException)
        {
            await FailAsync(db, job, "The AI provider request timed out. Try again with a narrower request.");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await FailAsync(db, job, ProviderErrorMessages.StoredCredentialUnreadable);
        }
        catch (FormatException)
        {
            await FailAsync(db, job, ProviderErrorMessages.StoredCredentialUnreadable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI build {JobId} failed unexpectedly.", job.Id);
            await FailAsync(db, job, "The build failed unexpectedly. Check the Debug Logs console for details.");
        }
    }

    private static async Task FailAsync(IPaaSContext db, AiBuildJob job, string error)
    {
        job.Status = AiBuildJobStatus.Failed;
        job.ProgressNote = "Build failed";
        job.Error = error.Length > 1000 ? error[..1000] : error;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static IntegrationFlowDto? DeserializeFlow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<IntegrationFlowDto>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
