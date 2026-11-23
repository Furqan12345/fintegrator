using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Api.Workers;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

public sealed class AiBuildJobTests : IDisposable
{

    private readonly string _databasePath;
    private readonly DbContextOptions<IPaaSContext> _options;

    public AiBuildJobTests()
    {
        // A file-backed database gives every context its own connection, matching
        // production concurrency (the worker and the test poller run side by side).
        _databasePath = Path.Combine(Path.GetTempPath(), $"ai-build-jobs-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite($"Data Source={_databasePath};Cache=Shared;Pooling=False")
            .Options;
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options);

    [Fact]
    public async Task Worker_ClaimsQueuedJobAndStoresCompletedDraft()
    {
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            context.AiProviderSettings.Add(new AiProviderSettings
            {
                Id = providerId,
                Name = "Azure",
                ProviderType = "OpenAICompatible",
                Endpoint = "http://localhost/v1/chat/completions",
                Model = "gpt-5.6-luna",
                EncryptedApiKey = "provider-key"
            });
            context.AiBuildJobs.Add(new AiBuildJob
            {
                Id = jobId,
                ProviderId = providerId,
                ProviderName = "Azure",
                Model = "gpt-5.6-luna",
                Instruction = "Fetch Shopify orders and save cross references.",
                Status = AiBuildJobStatus.Queued
            });
            await context.SaveChangesAsync();
        }

        var provider = BuildServices(ShopifyDraftHandler(), out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await WaitForStatusAsync(jobId, AiBuildJobStatus.Completed);
            Assert.Equal("Draft ready to review", job.ProgressNote);
            Assert.NotNull(job.ResultFlowJson);
            Assert.Contains("CrossReferenceStore", job.ResultFlowJson, StringComparison.Ordinal);
            Assert.Empty(job.Error);

            using var scope = provider.CreateScope();
            var tools = scope.ServiceProvider.GetRequiredService<AiFlowAgentTools>();
            var result = await tools.RunAsync("validate_draft",
                JsonSerializer.Serialize(new { draftJson = job.ResultFlowJson }), new List<string>(), CancellationToken.None);
            using var document = JsonDocument.Parse(result);
            Assert.True(document.RootElement.GetProperty("valid").GetBoolean(), result);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_ReportsProviderServiceUnavailableFor503()
    {
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            context.AiProviderSettings.Add(new AiProviderSettings
            {
                Id = providerId,
                Name = "Gemini",
                ProviderType = "OpenAICompatible",
                Endpoint = "http://localhost/v1/chat/completions",
                Model = "gemini-test",
                EncryptedApiKey = "provider-key"
            });
            context.AiBuildJobs.Add(new AiBuildJob
            {
                Id = jobId,
                ProviderId = providerId,
                Instruction = "Build a flow.",
                Status = AiBuildJobStatus.Queued
            });
            await context.SaveChangesAsync();
        }

        var provider = BuildServices(
            StubHttpMessageHandler.Json("{\"error\":{\"message\":\"temporarily overloaded\"}}", System.Net.HttpStatusCode.ServiceUnavailable),
            out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await WaitForStatusAsync(jobId, AiBuildJobStatus.Failed);
            // Unmapped/5xx statuses now state the HTTP status and carry the provider's own message.
            Assert.Contains("temporarily unavailable", job.Error);
            Assert.Contains("HTTP 503", job.Error);
            Assert.Contains("temporarily overloaded", job.Error);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_FailsStaleRunningJobsOnStartup()
    {
        var jobId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            context.AiBuildJobs.Add(new AiBuildJob
            {
                Id = jobId,
                ProviderId = Guid.NewGuid(),
                Instruction = "orphaned",
                Status = AiBuildJobStatus.Running,
                StartedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var provider = BuildServices(ShopifyDraftHandler(), out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await WaitForStatusAsync(jobId, AiBuildJobStatus.Failed);
            Assert.Contains("interrupted", job.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_RecordsProviderFailureInsteadOfThrowing()
    {
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            context.AiProviderSettings.Add(new AiProviderSettings
            {
                Id = providerId,
                Name = "Azure",
                ProviderType = "OpenAICompatible",
                Endpoint = "http://localhost/v1/chat/completions",
                Model = "gpt-5.6-luna",
                EncryptedApiKey = "provider-key"
            });
            context.AiBuildJobs.Add(new AiBuildJob
            {
                Id = jobId,
                ProviderId = providerId,
                Instruction = "Fetch Shopify orders.",
                Status = AiBuildJobStatus.Queued
            });
            await context.SaveChangesAsync();
        }

        var provider = BuildServices(StubHttpMessageHandler.Json("{\"error\":{\"message\":\"quota exhausted\"}}", System.Net.HttpStatusCode.TooManyRequests), out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await WaitForStatusAsync(jobId, AiBuildJobStatus.Failed);
            Assert.Contains("rate limit or quota", job.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private async Task<AiBuildJob> WaitForStatusAsync(Guid jobId, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var context = CreateContext();
            var job = await context.AiBuildJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId);
            if (job != null && job.Status == status) return job;
            await Task.Delay(100);
        }
        using var final = CreateContext();
        var last = await final.AiBuildJobs.AsNoTracking().FirstAsync(item => item.Id == jobId);
        Assert.Fail($"Job never reached '{status}'. Last status: {last.Status}, note: {last.ProgressNote}, error: {last.Error}");
        return last;
    }

    private static StubHttpMessageHandler ShopifyDraftHandler()
    {
        var triggerId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var draft = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Shopify orders",
                nodes = new object[]
                {
                    new { id = triggerId, nodeName = "Trigger", stepType = "Schedule", stepConfig = "{\"schedule\":{\"mode\":\"Cron\",\"cronExpression\":\"0 * * * *\"}}" },
                    new { id = storeId, nodeName = "Store orders", stepType = "CrossReferenceStore", stepConfig = "{\"listName\":\"shopify-orders\",\"arrayPath\":\"flowState.Fetch.orders\",\"keyPaths\":[\"id\"]}" }
                },
                edges = new object[]
                {
                    new { sourceNodeId = triggerId, targetNodeId = storeId, sourcePortId = "right", targetPortId = "left" }
                }
            },
            summary = "Draft fetches Shopify orders and stores cross-reference keys.",
            warnings = new[] { "Configure Shopify credentials before running." },
            sources = new[] { "Official documentation â€” https://shopify.dev/docs/api" }
        });
        return new StubHttpMessageHandler(_ =>
        {
            var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = draft } } } });
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        });
    }

    private ServiceProvider BuildServices(StubHttpMessageHandler handler, out AiBuildJobWorker worker)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Development));
        services.AddSingleton<IHostApplicationLifetime>(new TestLifetime());
        services.AddSingleton<IEncryptionService>(new PassthroughEncryptionService());
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        services.AddSingleton<ICrossReferenceRepository>(new StubCrossReferenceRepository());
        services.AddSingleton<IConnectionRepository>(new StubConnectionRepository());
        services.AddSingleton<IIntegrationRepository>(new StubIntegrationRepository());
        services.AddSingleton<IFlowTestRepository>(new StubFlowTestRepository());
        services.AddSingleton<IExecutionRepository>(new StubExecutionRepository());
        services.AddSingleton<IIntegrationCatalogRepository>(new StubIntegrationCatalogRepository());
        services.AddSingleton<ILogRepository>(new StubLogRepository());
        services.AddScoped(_ => new IPaaSContext(_options));
        services.AddScoped<AiFlowAgentTools>();
        services.AddScoped<AiFlowBuilderService>();
        var provider = services.BuildServiceProvider();
        worker = new AiBuildJobWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            NullLogger<AiBuildJobWorker>.Instance);
        return provider;
    }

    public void Dispose()
    {
        try { File.Delete(_databasePath); }
        catch (IOException) { }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}


