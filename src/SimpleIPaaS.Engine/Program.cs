using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Engine;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.Persistence;
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
if (DatabaseProvider.SupportsDatabaseLogging(builder.Configuration))
    builder.Logging.AddDatabaseLogging(builder.Configuration, "Engine");

// Every service this host owns, including the ones its hosted workers resolve per tick
// (FlowRunService for CronTriggerScheduler, DeadLetterService for replayed letters).
// EngineServiceRegistration is shared with the registration guard test so a missing
// registration fails the build's test run instead of silently breaking a worker.
builder.Services.AddEngineServices(builder.Configuration, builder.Environment);

// A graceful stop lets in-flight runs finish for Execution:ShutdownDrainSeconds; the host must wait at least that
// long (plus time to fail what is left and flush the packet queue) before it gives up on its services.
var drainSeconds = Math.Max(0, builder.Configuration.GetValue("Execution:ShutdownDrainSeconds", 25));
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(drainSeconds + 20));

var host = builder.Build();

// Ensure the shared schema exists / upgrades additively, and fail fast when the
// encryption key is missing or invalid outside Development (decryption of stored
// connection credentials is required to execute authenticated HTTP steps).
using (var scope = host.Services.CreateScope())
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    DatabaseStartup.EnsureSchema(builder.Configuration, host.Services, logger);

    _ = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

    logger.LogInformation("Engine ready: claiming Queued FlowExecutions from the shared database.");
}

host.Run();
