using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Infrastructure.Services.Auth;
using SimpleIPaaS.Infrastructure.Services.Email;
using SimpleIPaaS.Infrastructure.Services.Security;

namespace SimpleIPaaS.Engine;

// The Engine's service graph, kept in one place so the host (Program.cs) and the
// registration guard test (tests/SimpleIPaaS.Tests/Engine/EngineRegistrationTests.cs)
// build the exact same container.
//
// History: FlowRunService — which CronTriggerScheduler resolves on every cron tick —
// was registered in the API but not here, so every cron tick and every manual
// dead-letter replay died with "No service for type ... FlowRunService" while the API
// stayed healthy. Anything a hosted worker resolves must live in this method.
public static class EngineServiceRegistration
{
    public static IServiceCollection AddEngineServices(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<ExecutionOptions>(configuration.GetSection("Execution"));
        services.Configure<OperationsOptions>(configuration.GetSection("Operations"));

        // Shared persistence layer (identical registrations to the API). Both hosts must
        // land on the SAME database file; DefaultDatabasePath prevents per-project splits.
        services.AddIPaaSDatabase(configuration);
        services.AddEmailSender(configuration, environment);

        services.AddHttpClient();
        services.Configure<OutboundOptions>(configuration.GetSection("Outbound"));
        services.AddSingleton<OutboundUrlPolicy>();
        services.AddHttpClient("Outbound", (provider, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = provider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboundOptions>>().Value.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            });
        services.AddScoped<IIntegrationRepository, IntegrationRepository>();
        services.AddScoped<ICronScheduleRepository, IntegrationRepository>();
        services.AddScoped<IIntegrationCatalogRepository, IntegrationCatalogRepository>();
        services.AddScoped<IConnectionRepository, ConnectionRepository>();

        // Storage routing, identical to the API: this host writes packet logs and cross references, so
        // it has to make the same database-versus-Elasticsearch decision.
        services.AddIPaaSStorage();
        services.AddScoped<IExecutionRepository>(provider => new ExecutionRepository(
            provider.GetRequiredService<IPaaSContext>(),
            () => provider.GetRequiredService<StorageStoreRouter<IPacketLogStore>>().Current));
        services.AddScoped<ICrossReferenceRepository>(provider => new CrossReferenceRepository(
            provider.GetRequiredService<IPaaSContext>(),
            () => provider.GetRequiredService<StorageStoreRouter<ICrossReferenceEntryStore>>().Current));
        services.AddScoped<IFlowTestRepository, FlowTestRepository>();

        // Outbound transport stack used by the executor and dead-letter replays.
        services.AddScoped<ITransportEngine, TransportEngine>();
        services.AddScoped<IAuthenticationHandlerFactory, AuthenticationHandlerFactory>();
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddScoped<ICodeExecutionService, CodeExecutionService>();
        services.AddScoped<IAdvancedCodeExecutionService, AdvancedCodeExecutionService>();

        // Execution tier — engine-only concerns.
        services.AddSingleton<ExecutionCancellationRegistry>();
        services.AddScoped<FlowExecutor>();
        services.AddScoped<FlowTestResultRecorder>();
        services.AddScoped<DeadLetterService>();

        // Failure alerts: one digest per scan, sent through the shared email stack.
        services.AddSingleton<FailureAlertEmailBuilder>();
        services.AddScoped<FailureAlertService>();

        // FlowRunService persists Queued FlowExecution rows. CronTriggerScheduler resolves it on
        // every tick to enqueue a run; without this registration the scheduler logs an
        // InvalidOperationException per tick and no cron flow ever runs.
        services.AddScoped<FlowRunService>();

        services.AddHostedService<Workers.EngineHeartbeatWorker>();
        services.AddHostedService<SimpleIPaaS.Infrastructure.Services.ScriptWarmupService>();

        // Retention across both backends; identical registration to the API (idempotent passes).
        services.AddHostedService(provider => new StorageRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StorageRetentionWorker>>()));
        services.AddHostedService<Workers.FlowExecutionWorker>();
        services.AddHostedService<Workers.StaleExecutionRecoveryWorker>();
        services.AddHostedService<Workers.CronTriggerScheduler>();
        services.AddHostedService<Workers.DeadLetterWorker>();
        services.AddHostedService<Workers.FailureAlertWorker>();
        services.AddHostedService<Workers.DbCancellationWatcher>();

        return services;
    }
}
