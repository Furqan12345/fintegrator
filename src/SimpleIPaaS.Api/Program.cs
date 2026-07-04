using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Services;
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

// Configure CORS for Blazor WASM
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy =>
        {
            policy.AllowAnyOrigin()
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

// Background Worker
builder.Services.AddHostedService<FlowExecutionBackgroundWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");

// Tenant Middleware
app.Use(async (context, next) =>
{
    var tenantContext = context.RequestServices.GetRequiredService<ITenantContext>();
    
    // In a real application, extract tenant ID from JWT or custom header. 
    // Using a default Guid for MVP purposes.
    if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantIdHeader) && Guid.TryParse(tenantIdHeader, out var tenantId))
    {
        tenantContext.SetTenantId(tenantId);
    }
    else
    {
        tenantContext.SetTenantId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }
    
    await next();
});

app.UseAuthorization();

app.MapControllers();

// Ensure the SQLite database exists without wiping persisted local data.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
    DatabaseSchemaInitializer.EnsureUpToDate(db);
}

app.Run();
