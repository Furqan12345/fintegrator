using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.HealthChecks;
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

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
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
builder.Services.AddScoped<IIntegrationCatalogRepository, IntegrationCatalogRepository>();
builder.Services.AddScoped<IConnectionRepository, ConnectionRepository>();
builder.Services.AddScoped<IExecutionRepository, ExecutionRepository>();

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
    }
}

app.Run();
