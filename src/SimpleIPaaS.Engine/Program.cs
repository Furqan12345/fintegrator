using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Engine.Workers;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.MultiTenancy;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Infrastructure.Services.Auth;
using SimpleIPaaS.Infrastructure.Services.Security;

// SimpleIPaaS.Engine — the standalone execution tier.
//
// This process runs ALL flows. It talks to the rest of the platform exclusively
// through the shared database: it claims Queued FlowExecution rows written by the
// API, owns cron/one-time scheduling against IntegrationFlows rows saved by the
// designer, honours Cancelled rows, and performs dead-letter replays flagged by
// the API. It intentionally exposes no HTTP endpoints and never calls the API.
var builder = Host.CreateApplicationBuilder(args);

// Mirror every log line into the shared database so the UI's Debug Logs console can tail
// what this out-of-process worker is doing. Batched and non-blocking; see
// SimpleIPaaS.Infrastructure.Logging.DatabaseLoggerProvider.
builder.Logging.AddDatabaseLogging(builder.Configuration, "Engine");

builder.Services.Configure<ExecutionOptions>(builder.Configuration.GetSection("Execution"));

// Shared persistence layer (identical registrations to the API). Both hosts must
// land on the SAME database file; DefaultDatabasePath prevents per-project splits.
builder.Services.AddDbContext<IPaaSContext>(options =>
    options.UseSqlite(DefaultDatabasePath.Resolve(builder.Configuration.GetConnectionString("DefaultConnection"))));
builder.Services.AddScoped<ITenantContext, TenantContext>();

builder.Services.AddHttpClient();
builder.Services.AddScoped<IIntegrationRepository, IntegrationRepository>();
builder.Services.AddScoped<ICronScheduleRepository, IntegrationRepository>();
builder.Services.AddScoped<IIntegrationCatalogRepository, IntegrationCatalogRepository>();
builder.Services.AddScoped<IConnectionRepository, ConnectionRepository>();
builder.Services.AddScoped<IExecutionRepository, ExecutionRepository>();
builder.Services.AddScoped<ICrossReferenceRepository, CrossReferenceRepository>();

// Outbound transport stack used by the executor and dead-letter replays.
builder.Services.AddScoped<ITransportEngine, TransportEngine>();
builder.Services.AddScoped<IAuthenticationHandlerFactory, AuthenticationHandlerFactory>();
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddScoped<ICodeExecutionService, CodeExecutionService>();
builder.Services.AddScoped<IAdvancedCodeExecutionService, AdvancedCodeExecutionService>();

// Execution tier — engine-only concerns.
builder.Services.AddSingleton<ExecutionCancellationRegistry>();
builder.Services.AddScoped<FlowExecutor>();
builder.Services.AddScoped<DeadLetterService>();

builder.Services.AddHostedService<FlowExecutionWorker>();
builder.Services.AddHostedService<CronTriggerScheduler>();
builder.Services.AddHostedService<DeadLetterWorker>();
builder.Services.AddHostedService<DbCancellationWatcher>();

var host = builder.Build();

// Ensure the shared schema exists / upgrades additively, and fail fast when the
// encryption key is missing or invalid outside Development (decryption of stored
// connection credentials is required to execute authenticated HTTP steps).
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
    DatabaseSchemaInitializer.EnsureUpToDate(db);

    _ = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    logger.LogInformation("Engine ready: claiming Queued FlowExecutions from the shared database.");
}

host.Run();
