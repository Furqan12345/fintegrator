using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Api;
using SimpleIPaaS.Api.HealthChecks;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Api.Observability;
using SimpleIPaaS.Api.Security;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Api.Middleware;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Infrastructure.Services.Auth;
using SimpleIPaaS.Infrastructure.Services.Security;
using SimpleIPaaS.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

// Mirror API log lines into the shared database alongside the Engine's, so the Debug Logs
// console shows both tiers. Batched and non-blocking; EF Core's own SQL logging is
// excluded by the provider to prevent a write/read feedback loop.
if (DatabaseProvider.SupportsDatabaseLogging(builder.Configuration))
    builder.Logging.AddDatabaseLogging(builder.Configuration, "Api");

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2_000_000);

// Add services to the container.
builder.Services.AddControllers(options => options.Filters.Add<ViewerReadOnlyFilter>());
builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Platform service graph (persistence, repositories, transport, AI workers, health checks).
// Kept in ApiServiceRegistration so the registration guard test builds the same container the
// running host uses.
builder.Services.AddApiPlatformServices(builder.Configuration);
builder.Services.AddAppAuthentication(builder.Configuration, builder.Environment);

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

var app = builder.Build();


app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error != null)
        {
            // Log through the request's logger factory under an explicit category so the entry is
            // never filtered out and reaches the same sink as the rest of the request's logs
            // (database or Elasticsearch). The message carries the trace id in text form so
            // /api/logs?search=<traceId> finds it.
            var loggerFactory = context.RequestServices.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger(UnhandledExceptionLogger.CategoryName) ?? app.Logger;
            UnhandledExceptionLogger.Log(logger, context, feature.Error);
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

// The API is only reachable through Caddy/App Service; trust the proxy headers for scheme/IP.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownIPNetworks = { },
    KnownProxies = { }
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
    context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
    context.Response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    await next();
});

app.UseDefaultFiles();
var staticContentTypes = new FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".dat"] = "application/octet-stream";
// Static assets: serve the publish-time .br/.gz twins; everything else (JSON, HTML) goes through response compression.
SimpleIPaaS.Api.Hosting.PrecompressedStaticFiles.UsePrecompressedStaticFiles(app, app.Environment.WebRootFileProvider);
app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = new SimpleIPaaS.Api.Hosting.PrecompressedContentTypeProvider(staticContentTypes) });
app.UseMiddleware<OperationalMetricsMiddleware>();

app.UseRouting();
app.UseCors("Default");
app.UseAuthentication();
app.UseMiddleware<AuthContextMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<MetricsAuthMiddleware>();
app.UseAuthorization();

app.MapControllers();
if (app.Configuration.GetValue<bool>("Gateway:Enabled"))
    app.MapFallbackToFile("index.html").AllowAnonymous();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("self")
}).AllowAnonymous();

// Ensure the SQLite database exists without wiping persisted local data.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IPaaSContext>();
    DatabaseSchemaInitializer.EnsureUpToDate(db, app.Logger);

    // Fail fast if the encryption key is missing/invalid outside Development.
    _ = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

    if (app.Environment.IsDevelopment())
    {
        // Seed the shipped demo flow (SP-API N+1 fan-out + nested cross-reference filter)
        // so it is runnable from the Flow Designer immediately.
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
                .Include(flow => flow.Nodes)
                .FirstOrDefaultAsync(f => f.Name == sampleFlowName);

            IntegrationFlow? demoFlow = existing;

            if (existing != null)
            {
                // Archived nodes are included on purpose: a demo repair may need to see them.
                existing.Nodes = await db.IntegrationSteps.IgnoreQueryFilters()
                    .Where(node => node.FlowId == existing.Id)
                    .ToListAsync();
            }

            if (existing != null && RepairAmazonDemoFlow(existing))
            {
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                app.Logger.LogInformation("Repaired Amazon demo cross-reference key paths for the current flow shape.");
            }
            if (existing != null && !reseedDemoFlow)
            {
                app.Logger.LogInformation(
                    "Demo flow '{FlowName}' already exists and was left untouched. Set Demo:ReseedOnStartup=true to replace it from the sample (this deletes the flow and its execution history).",
                    sampleFlowName);
            }
            else if (existing != null)
            {
                var executionIds = await db.FlowExecutions
                    .Where(execution => execution.FlowId == existing.Id)
                    .Select(execution => execution.Id)
                    .ToListAsync();

                if (executionIds.Count > 0)
                {
                    // Packet logs hang off step executions, not flow executions, so they have to be
                    // resolved through the step ids or they survive the reseed as orphans.
                    var stepExecutionIds = await db.StepExecutions
                        .Where(step => executionIds.Contains(step.FlowExecutionId))
                        .Select(step => step.Id)
                        .ToListAsync();

                    if (stepExecutionIds.Count > 0)
                    {
                        await ColdStorage.DeleteForStepsAsync(db, db.StepExecutions
                            .Where(step => stepExecutionIds.Contains(step.Id))
                            .Select(step => step.Id));
                        await db.StepPacketLogs
                            .Where(packet => stepExecutionIds.Contains(packet.StepExecutionId))
                            .ExecuteDeleteAsync();
                    }

                    await db.DeadLetterEntries
                        .Where(entry => executionIds.Contains(entry.FlowExecutionId))
                        .ExecuteDeleteAsync();
                    await db.StepExecutions
                        .Where(step => executionIds.Contains(step.FlowExecutionId))
                        .ExecuteDeleteAsync();
                    await db.FlowExecutions
                        .Where(execution => executionIds.Contains(execution.Id))
                        .ExecuteDeleteAsync();
                }

                var oldTestCaseIds = await db.FlowTestCases
                    .Where(test => test.FlowId == existing.Id)
                    .Select(test => test.Id)
                    .ToListAsync();
                if (oldTestCaseIds.Count > 0)
                {
                    await db.FlowTestRuns
                        .Where(run => oldTestCaseIds.Contains(run.TestCaseId))
                        .ExecuteDeleteAsync();
                    await db.FlowTestCases
                        .Where(test => oldTestCaseIds.Contains(test.Id))
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
                // The shipped sample carries fixed GUIDs. If they are already present (for
                // example in a database migrated from another environment) inserting it again
                // would collide with existing rows, so leave the data untouched.
                var sampleNodeIds = flow.Nodes.Select(node => node.Id).ToList();
                // Include archived nodes too: a fixed sample GUID must not collide with one.
                if (await db.IntegrationSteps.IgnoreQueryFilters().AnyAsync(node => sampleNodeIds.Contains(node.Id)))
                {
                    app.Logger.LogInformation(
                        "Demo sample node ids already exist in this database; skipping demo seeding to avoid colliding with existing flows.");
                    demoFlow = null;
                }
                else
                {
                    foreach (var node in flow.Nodes)
                    {
                        node.FlowId = flow.Id;
                    }

                    foreach (var edge in flow.Edges)
                    {
                        edge.FlowId = flow.Id;
                    }

                    db.IntegrationFlows.Add(flow);
                    demoFlow = flow;
                    await db.SaveChangesAsync();
                    app.Logger.LogInformation(
                        "Seeded demo flow '{FlowName}' (id={FlowId})",
                        flow.Name, flow.Id);
                }
            }

            await SeedAmazonDemoTestAsync(db, demoFlow);
        }
        else
        {
            app.Logger.LogWarning(
                "Demo sample flow not found at samples/integrations/sp-api-orders-nplus1.json; skipping seed.");
        }
    }
}

static bool RepairAmazonDemoFlow(IntegrationFlow flow)
{
    var changed = false;
    var filter = flow.Nodes.FirstOrDefault(node => node.NodeName == "FilterNewSKUs");
    var store = flow.Nodes.FirstOrDefault(node => node.NodeName == "RememberSeenSKUs");

    foreach (var node in new[] { filter, store }.Where(node => node != null))
    {
        try
        {
            var root = JObject.Parse(node!.StepConfig);
            if (root["keyPaths"] is JArray keyPaths && keyPaths.Count == 1 &&
                string.Equals(keyPaths[0]?.ToString(), "sku", StringComparison.OrdinalIgnoreCase))
            {
                keyPaths[0] = "SellerSKU";
                changed = true;
            }

            if (node == store && root["valuePaths"] is JArray valuePaths && valuePaths.Count > 0 &&
                valuePaths.Any(path => string.Equals(path?.ToString(), "qty", StringComparison.OrdinalIgnoreCase)))
            {
                root["valuePaths"] = new JArray("QuantityOrdered");
                changed = true;
            }

            node.StepConfig = root.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception)
        {
            // Leave malformed custom configuration untouched; normal flow validation reports it.
        }
    }

    return changed;
}
static async Task SeedAmazonDemoTestAsync(IPaaSContext db, IntegrationFlow? flow)
{
    if (flow == null) return;

    const string testName = "Amazon SP-API offline happy path";
    var existingTest = await db.FlowTestCases.FirstOrDefaultAsync(test =>
        test.FlowId == flow.Id && test.Name == testName);
    var fetchOrders = flow.Nodes.FirstOrDefault(node => node.NodeName == "FetchOrders");
    var fetchLineItems = flow.Nodes.FirstOrDefault(node => node.NodeName == "FetchLineItems");
    var filterNode = flow.Nodes.FirstOrDefault(node => node.NodeName == "FilterNewOrders")
        ?? flow.Nodes.FirstOrDefault(node => node.NodeName == "FilterNewSKUs");
    var processNewItems = flow.Nodes.FirstOrDefault(node => node.NodeName == "ProcessNewItems");
    if (fetchOrders == null || fetchLineItems == null || filterNode == null)
    {
        return;
    }

    var isSkuVariant = string.Equals(filterNode.NodeName, "FilterNewSKUs", StringComparison.OrdinalIgnoreCase);
    var fixtureListName = isSkuVariant ? "processed-skus" : "processed-orders";
    var fixtureKey = isSkuVariant ? "SKU-002" : "ORDER-ALREADY-DONE";
    var retainedValue = isSkuVariant ? "SKU-001" : "ORDER-100";
    var removedValue = isSkuVariant ? "SKU-002" : "ORDER-ALREADY-DONE";
    var definition = new FlowTestDefinition
    {
        TriggerPayloadJson = "{}",
        HttpResponses = new Dictionary<string, List<FlowHttpResponseOverride>>
        {
            [fetchOrders.Id.ToString()] = new()
            {
                new FlowHttpResponseOverride
                {
                    StatusCode = 200,
                    ResponseBody = "{\"payload\":{\"Orders\":[{\"AmazonOrderId\":\"ORDER-100\",\"OrderStatus\":\"Shipped\"},{\"AmazonOrderId\":\"ORDER-ALREADY-DONE\",\"OrderStatus\":\"Shipped\"}]}}"
                }
            },
            [fetchLineItems.Id.ToString()] = new()
            {
                new FlowHttpResponseOverride
                {
                    StatusCode = 200,
                    ResponseBody = "{\"payload\":{\"OrderItems\":[{\"SellerSKU\":\"SKU-001\",\"QuantityOrdered\":1}]}}"
                },
                new FlowHttpResponseOverride
                {
                    StatusCode = 200,
                    ResponseBody = "{\"payload\":{\"OrderItems\":[{\"SellerSKU\":\"SKU-002\",\"QuantityOrdered\":2}]}}"
                }
            }
        },
        CrossReferenceRows = new List<FlowCrossReferenceFixture>
        {
            new() { ListName = fixtureListName, KeyValue = fixtureKey, ValueJson = "{\"source\":\"seed\"}" }
        },
        Assertions = new List<FlowTestAssertion>
        {
            new() { Type = "ExecutionStatus", Operator = "Equals", ExpectedValue = "Success" },
            new() { Type = "Node", NodeName = filterNode.NodeName, Field = "ResponsePayload", Operator = "Contains", ExpectedValue = retainedValue },
            new() { Type = "Node", NodeName = filterNode.NodeName, Field = "ResponsePayload", Operator = "NotContains", ExpectedValue = removedValue }
        }
    };

    if (processNewItems != null)
    {
        definition.HttpResponses[processNewItems.Id.ToString()] = new()
        {
            new FlowHttpResponseOverride { StatusCode = 200, ResponseBody = "{\"accepted\":true}" }
        };
    }

    if (existingTest != null)
    {
        var storedDefinition = JsonSerializer.Deserialize<FlowTestDefinition>(existingTest.DefinitionJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var needsRepair = isSkuVariant &&
            (storedDefinition?.CrossReferenceRows?.Any(row => row.ListName.Equals("processed-orders", StringComparison.OrdinalIgnoreCase)) == true
             || storedDefinition?.Assertions?.Any(assertion =>
                 assertion.NodeName.Equals(filterNode.NodeName, StringComparison.OrdinalIgnoreCase)
                 && assertion.Operator.Equals("NotContains", StringComparison.OrdinalIgnoreCase)
                 && assertion.ExpectedValue.TrimStart().StartsWith("{", StringComparison.Ordinal)) == true);
        if (!needsRepair)
        {
            return;
        }

        existingTest.DefinitionJson = JsonSerializer.Serialize(definition);
        existingTest.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return;
    }
    db.FlowTestCases.Add(new FlowTestCase
    {
        Id = Guid.NewGuid(),
        FlowId = flow.Id,
        Name = testName,
        Description = "Runs the Amazon SP-API demo entirely offline with mocked orders, line items, and a seeded processed-order key.",
        Enabled = true,
        RequiredForPublish = false,
        DefinitionJson = JsonSerializer.Serialize(definition),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    });
    await db.SaveChangesAsync();
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

public partial class Program { }
