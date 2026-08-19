using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.HealthChecks;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Api.Middleware;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Infrastructure.Services.Auth;
using SimpleIPaaS.Infrastructure.Services.Security;
using SimpleIPaaS.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure CORS allow-list for the Blazor WASM client
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5001", "https://localhost:5001", "http://localhost:5000" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("Default",
        policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});

// Configure EF Core SQLite
builder.Services.AddDbContext<IPaaSContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=ipaas.db"));

// Multi-tenancy
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Dependency Injection
builder.Services.AddHttpClient();
builder.Services.AddScoped<IIntegrationRepository, IntegrationRepository>();
builder.Services.AddScoped<ICronScheduleRepository, IntegrationRepository>();
builder.Services.AddScoped<IIntegrationCatalogRepository, IntegrationCatalogRepository>();
builder.Services.AddScoped<IConnectionRepository, ConnectionRepository>();
builder.Services.AddScoped<IExecutionRepository, ExecutionRepository>();
builder.Services.AddScoped<ICrossReferenceRepository, CrossReferenceRepository>();

builder.Services.AddScoped<ITransportEngine, TransportEngine>();
builder.Services.AddScoped<IAuthenticationHandlerFactory, AuthenticationHandlerFactory>();
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddScoped<ICodeExecutionService, CodeExecutionService>();
builder.Services.AddScoped<IAdvancedCodeExecutionService, AdvancedCodeExecutionService>();

builder.Services.AddScoped<FlowExecutor>();
builder.Services.AddScoped<DeadLetterService>();
builder.Services.AddScoped<FlowRunService>();

// Execution engine
builder.Services.Configure<ExecutionOptions>(builder.Configuration.GetSection("Execution"));
builder.Services.AddSingleton<IExecutionQueue, ChannelExecutionQueue>();
builder.Services.AddSingleton<ExecutionCancellationRegistry>();

// Background Workers
builder.Services.AddHostedService<FlowExecutionWorker>();
builder.Services.AddHostedService<CronTriggerScheduler>();
builder.Services.AddHostedService<FlowExecutionBackgroundWorker>();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error != null)
        {
            app.Logger.LogError(
                feature.Error,
                "Unhandled exception processing {Method} {Path}",
                context.Request.Method,
                feature.Path ?? context.Request.Path.Value);
        }

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Detail = "The request could not be completed. Quote the trace identifier when reporting this problem.",
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;

        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    });
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Default");

// API-key authentication + tenant resolution
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

// Ensure the SQLite database exists without wiping persisted local data.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
    DatabaseSchemaInitializer.EnsureUpToDate(db);

    // Fail fast if the encryption key is missing/invalid outside Development.
    _ = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

    if (app.Environment.IsDevelopment())
    {
        const string devApiKey = "dev-api-key";
        var devKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(devApiKey)));
        if (!await db.ApiKeys.AnyAsync(k => k.KeyHash == devKeyHash))
        {
            db.ApiKeys.Add(new ApiKey
            {
                Id = Guid.NewGuid(),
                TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Development",
                KeyHash = devKeyHash,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        app.Logger.LogInformation("Development API key provisioned. Send it via the X-Api-Key header: {ApiKey}", devApiKey);

        // Seed the shipped demo flow (SP-API N+1 fan-out + nested cross-reference filter)
        // for the dev tenant so it is runnable from the Flow Designer immediately.
        var devTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        // Destructive demo reseed is opt-in: it drops the sample flow and every execution,
        // step, packet log and dead-letter row belonging to it. Enable with
        // "Demo:ReseedOnStartup": true in appsettings.Development.json, or DEMO_RESEED=true.
        var reseedDemoFlow = app.Configuration.GetValue<bool>("Demo:ReseedOnStartup")
            || string.Equals(Environment.GetEnvironmentVariable("DEMO_RESEED"), "true", StringComparison.OrdinalIgnoreCase);
        var samplePath = ResolveSamplePath();
        if (samplePath != null)
        {
            const string sampleFlowName = "Amazon SP-API Orders N+1 Sync + Nested Dedup";

            var existing = await db.IntegrationFlows
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(f => f.Name == sampleFlowName && f.TenantId == devTenantId);

            if (existing != null && !reseedDemoFlow)
            {
                app.Logger.LogInformation(
                    "Demo flow '{FlowName}' already exists and was left untouched. Set Demo:ReseedOnStartup=true to replace it from the sample (this deletes the flow and its execution history).",
                    sampleFlowName);
            }
            else if (existing != null)
            {
                var executionIds = await db.FlowExecutions
                    .IgnoreQueryFilters()
                    .Where(execution => execution.FlowId == existing.Id && execution.TenantId == devTenantId)
                    .Select(execution => execution.Id)
                    .ToListAsync();

                if (executionIds.Count > 0)
                {
                    // Packet logs hang off step executions, not flow executions, so they have to be
                    // resolved through the step ids or they survive the reseed as orphans.
                    var stepExecutionIds = await db.StepExecutions
                        .IgnoreQueryFilters()
                        .Where(step => executionIds.Contains(step.FlowExecutionId))
                        .Select(step => step.Id)
                        .ToListAsync();

                    if (stepExecutionIds.Count > 0)
                    {
                        await db.StepPacketLogs
                            .IgnoreQueryFilters()
                            .Where(packet => stepExecutionIds.Contains(packet.StepExecutionId))
                            .ExecuteDeleteAsync();
                    }

                    await db.DeadLetterEntries
                        .IgnoreQueryFilters()
                        .Where(entry => executionIds.Contains(entry.FlowExecutionId))
                        .ExecuteDeleteAsync();
                    await db.StepExecutions
                        .IgnoreQueryFilters()
                        .Where(step => executionIds.Contains(step.FlowExecutionId))
                        .ExecuteDeleteAsync();
                    await db.FlowExecutions
                        .IgnoreQueryFilters()
                        .Where(execution => executionIds.Contains(execution.Id))
                        .ExecuteDeleteAsync();
                }

                db.IntegrationFlows.Remove(existing);
                await db.SaveChangesAsync();
                app.Logger.LogInformation(
                    "Removed existing demo flow '{FlowName}' (id={FlowId}) to re-seed from latest sample.",
                    existing.Name, existing.Id);
            }

            if (existing == null || reseedDemoFlow)
            {
                var sampleJson = await File.ReadAllTextAsync(samplePath);
                var sampleDto = JsonSerializer.Deserialize<IntegrationFlowDto>(sampleJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Sample flow failed to deserialize.");

                var flow = sampleDto.ToEntity();
                flow.TenantId = devTenantId;
                foreach (var node in flow.Nodes)
                {
                    node.TenantId = devTenantId;
                    node.FlowId = flow.Id;
                }

                foreach (var edge in flow.Edges)
                {
                    edge.TenantId = devTenantId;
                    edge.FlowId = flow.Id;
                }

                db.IntegrationFlows.Add(flow);
                await db.SaveChangesAsync();
                app.Logger.LogInformation(
                    "Seeded demo flow '{FlowName}' (id={FlowId}) for dev tenant {TenantId}",
                    flow.Name, flow.Id, devTenantId);
            }
        }
        else
        {
            app.Logger.LogWarning(
                "Demo sample flow not found at samples/integrations/sp-api-orders-nplus1.json; skipping seed.");
        }
    }
}

static string? ResolveSamplePath()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir.Parent != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "SimpleIPaaS.slnx")))
        {
            return Path.Combine(dir.FullName, "samples", "integrations", "sp-api-orders-nplus1.json");
        }

        dir = dir.Parent;
    }

    return null;
}

app.Run();
