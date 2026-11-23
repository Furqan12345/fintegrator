using System.Net;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.HealthChecks;
using SimpleIPaaS.Api.Observability;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Infrastructure.Services.Auth;
using SimpleIPaaS.Infrastructure.Services.Security;

namespace SimpleIPaaS.Api;

// The API host's platform service graph (persistence, repositories, transport, AI workers,
// health checks), extracted from Program.cs so the registration guard test can build a
// container from exactly the same list the running host uses. See
// tests/SimpleIPaaS.Tests/Engine/EngineRegistrationTests.cs.
public static class ApiServiceRegistration
{
    public static IServiceCollection AddApiPlatformServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();

        // Dependency Injection
        services.AddHttpClient();
        services.Configure<OutboundOptions>(configuration.GetSection("Outbound"));
        services.AddSingleton<OutboundUrlPolicy>();
        services.AddHttpClient("Outbound", (provider, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = provider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboundOptions>>().Value.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            });
        services.AddHttpClient("AiProvider", client => client.Timeout = TimeSpan.FromSeconds(300))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All });
        services.AddHttpClient("AiResearch", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All });
        services.AddScoped<AiFlowAgentTools>();
        services.AddScoped<AiBuildSettingsProvider>();
        services.AddScoped<AiFlowBuilderService>();
        services.AddHostedService<Workers.AiBuildJobWorker>();
        services.AddHostedService<Workers.AiChatWorker>();

        // EF Core on SQLite (default), SQL Server or PostgreSQL ("Database:Provider"). This API and
        // SimpleIPaaS.Engine must land on the SAME database; both register it through AddIPaaSDatabase.
        services.AddIPaaSDatabase(configuration);

        // Retention for the relational datasets and for Elasticsearch indices whose ILM delete phase is
        // switched off. Registered in both hosts: every pass is idempotent, so whichever process is alive
        // keeps retention moving.
        services.AddHostedService(provider => new StorageRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<StorageRetentionWorker>>()));
        services.AddScoped<IIntegrationRepository, IntegrationRepository>();
        services.AddScoped<ICronScheduleRepository, IntegrationRepository>();
        services.AddScoped<IIntegrationCatalogRepository, IntegrationCatalogRepository>();
        services.AddScoped<IConnectionRepository, ConnectionRepository>();

        // Storage routing: which growing dataset lives in the relational database and which in
        // Elasticsearch, plus the retention policy behind both. Registered before the repositories
        // because ExecutionRepository and CrossReferenceRepository delegate their growing halves to the
        // stores this resolves.
        services.AddIPaaSStorage();
        services.AddScoped<IExecutionRepository>(provider => new ExecutionRepository(
            provider.GetRequiredService<IPaaSContext>(),
            () => provider.GetRequiredService<StorageStoreRouter<IPacketLogStore>>().Current));
        services.AddScoped<ICrossReferenceRepository>(provider => new CrossReferenceRepository(
            provider.GetRequiredService<IPaaSContext>(),
            () => provider.GetRequiredService<StorageStoreRouter<ICrossReferenceEntryStore>>().Current));
        services.AddScoped<IFlowTestRepository, FlowTestRepository>();

        services.AddScoped<ITransportEngine, TransportEngine>();
        services.AddScoped<IAuthenticationHandlerFactory, AuthenticationHandlerFactory>();
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddScoped<ICodeExecutionService, CodeExecutionService>();
        services.AddScoped<IAdvancedCodeExecutionService, AdvancedCodeExecutionService>();

        // FlowRunService only *persists runs*: it writes a Queued FlowExecution row that the
        // standalone SimpleIPaaS.Engine claims from the shared database.
        services.AddScoped<FlowRunService>();

        // Debug Mode runs a single card inline so the designer can step through a flow. It is the
        // only synchronous execution in the API: it never enqueues, never schedules and never
        // dead-letters, and a failure is returned to the caller instead.
        services.AddScoped<FlowExecutor>();
        services.AddScoped<FlowTestResultRecorder>();

        // Interactive test runs execute inside the API on a bounded background channel. Their
        // FlowExecutions start InProgress, so the Engine's Queued-only claim never sees them.
        services.AddSingleton<FlowTestRunner>();
        services.AddHostedService(provider => provider.GetRequiredService<FlowTestRunner>());

        // Health checks
        services.Configure<OperationsOptions>(configuration.GetSection("Operations"));

        services.AddSingleton<OperationalMetrics>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "self", "ready" })
            .AddCheck<EngineHealthCheck>("engine", tags: new[] { "ready" });

        return services;
    }
}
