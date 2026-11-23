using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

public sealed class AiChatWorkerTests : IDisposable
{

    private readonly string _databasePath;
    private readonly DbContextOptions<IPaaSContext> _options;

    public AiChatWorkerTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"ai-chat-turns-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite($"Data Source={_databasePath};Cache=Shared;Pooling=False")
            .Options;
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options);

    [Fact]
    public async Task Worker_ClaimsQueuedTurnAndStoresStepsSummaryAndSessionDraft()
    {
        var providerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var userMessageId = Guid.NewGuid();
        var agentMessageId = Guid.NewGuid();
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
            context.AiChatSessions.Add(new AiChatSession
            {
                Id = sessionId,
                Title = "New build session",
                LastProviderId = providerId
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                Id = userMessageId,
                SessionId = sessionId,
                Role = AiChatMessageRole.User,
                Content = "Fetch Shopify orders and save cross references.",
                Status = AiChatMessageStatus.Complete
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                Id = agentMessageId,
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Instruction = "Fetch Shopify orders and save cross references.",
                Status = AiChatMessageStatus.Queued,
                ProviderId = providerId,
                ProviderName = "Gemini",
                Model = "gemini-test"
            });
            await context.SaveChangesAsync();
        }

        var provider = BuildServices(ResearchThenDraftHandler(), out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var message = await WaitForStatusAsync(agentMessageId, AiChatMessageStatus.Complete);
            Assert.Empty(message.Error);
            Assert.Equal("Draft fetches Shopify orders and stores cross-reference keys.", message.Content);
            Assert.NotNull(message.ResultFlowJson);
            Assert.Contains("CrossReferenceStore", message.ResultFlowJson, StringComparison.Ordinal);
            Assert.Contains("Configure Shopify credentials before running.", message.WarningsJson);

            var steps = JsonSerializer.Deserialize<List<AiChatStepDto>>(message.StepsJson) ?? new List<AiChatStepDto>();
            Assert.NotEmpty(steps);
            Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Note)));

            using var scope = provider.CreateScope();
            var tools = scope.ServiceProvider.GetRequiredService<AiFlowAgentTools>();
            var result = await tools.RunAsync("validate_draft",
                JsonSerializer.Serialize(new { draftJson = message.ResultFlowJson }), new List<string>(), CancellationToken.None);
            using var document = JsonDocument.Parse(result);
            Assert.True(document.RootElement.GetProperty("valid").GetBoolean(), result);

            using var final = CreateContext();
            var session = await final.AiChatSessions.AsNoTracking().FirstAsync(item => item.Id == sessionId);
            Assert.Equal(message.ResultFlowJson, session.DraftFlowJson);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_RecordsActionableErrorWhenProviderRateLimits()
    {
        var providerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var agentMessageId = Guid.NewGuid();
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
            context.AiChatSessions.Add(new AiChatSession { Id = sessionId});
            context.AiChatMessages.Add(new AiChatMessage
            {
                Id = agentMessageId,
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Instruction = "Build a flow.",
                Status = AiChatMessageStatus.Queued,
                ProviderId = providerId
            });
            await context.SaveChangesAsync();
        }

        var stub = new StubHttpMessageHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new System.Net.Http.StringContent("{\"error\":{\"message\":\"quota exhausted\"}}", System.Text.Encoding.UTF8, "application/json")
        });
        BuildServices(stub, out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var message = await WaitForStatusAsync(agentMessageId, AiChatMessageStatus.Failed);
            Assert.Contains("rate limit", message.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_AnswersCasualPromptWithoutCallingToolsOrBuilding()
    {
        var providerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var agentMessageId = Guid.NewGuid();
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
            context.AiChatSessions.Add(new AiChatSession { Id = sessionId});
            context.AiChatMessages.Add(new AiChatMessage
            {
                SessionId = sessionId,
                Role = AiChatMessageRole.User,
                Content = "how are you",
                Status = AiChatMessageStatus.Complete
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                Id = agentMessageId,
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Instruction = "how are you",
                Status = AiChatMessageStatus.Queued,
                ProviderId = providerId
            });
            await context.SaveChangesAsync();
        }

        var stub = new StubHttpMessageHandler(_ =>
        {
            var body = "{\"choices\":[{\"message\":{\"content\":\"I'm doing well! What flow should we build today?\"}}]}";
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        });
        BuildServices(stub, out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var message = await WaitForStatusAsync(agentMessageId, AiChatMessageStatus.Complete);
            Assert.Equal("I'm doing well! What flow should we build today?", message.Content);
            Assert.Null(message.ResultFlowJson);
            Assert.Empty(message.Error);
            Assert.All(stub.RequestBodies, body => Assert.DoesNotContain("\"tools\"", body, StringComparison.Ordinal));

            using var final = CreateContext();
            var session = await final.AiChatSessions.AsNoTracking().FirstAsync(item => item.Id == sessionId);
            Assert.Null(session.DraftFlowJson);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_TreatsPlainModelReplyAsConversationWithoutDraft()
    {
        var providerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var agentMessageId = Guid.NewGuid();
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
            context.AiChatSessions.Add(new AiChatSession { Id = sessionId});
            context.AiChatMessages.Add(new AiChatMessage
            {
                SessionId = sessionId,
                Role = AiChatMessageRole.User,
                Content = "what do you think about this?",
                Status = AiChatMessageStatus.Complete
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                Id = agentMessageId,
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Instruction = "what do you think about this?",
                Status = AiChatMessageStatus.Queued,
                ProviderId = providerId
            });
            await context.SaveChangesAsync();
        }

        var stub = new StubHttpMessageHandler(request =>
        {
            var raw = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var content = raw.Contains("intent router", StringComparison.Ordinal)
                ? "CHAT"
                : "It looks good so far. Tell me which flow you want to change.";
            return ChatResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
        });
        BuildServices(stub, out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var message = await WaitForStatusAsync(agentMessageId, AiChatMessageStatus.Complete);
            Assert.Equal("It looks good so far. Tell me which flow you want to change.", message.Content);
            Assert.Null(message.ResultFlowJson);
            Assert.Empty(message.Error);

            using var final = CreateContext();
            var session = await final.AiChatSessions.AsNoTracking().FirstAsync(item => item.Id == sessionId);
            Assert.Null(session.DraftFlowJson);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_PassesConversationHistoryToRouterAndBuildRequests()
    {
        var providerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var agentMessageId = Guid.NewGuid();
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
            context.AiChatSessions.Add(new AiChatSession { Id = sessionId});
            context.AiChatMessages.Add(new AiChatMessage
            {
                SessionId = sessionId,
                Role = AiChatMessageRole.User,
                Content = "Use the Shopify connection I saved yesterday.",
                Status = AiChatMessageStatus.Complete
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Content = "Got it, I will use that connection.",
                Status = AiChatMessageStatus.Complete
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                Id = agentMessageId,
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Instruction = "Add a debug node at the end",
                Status = AiChatMessageStatus.Queued,
                ProviderId = providerId
            });
            await context.SaveChangesAsync();
        }

        var fetchNodeId = Guid.NewGuid();
        var draft = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Shopify orders with debug",
                nodes = new object[]
                {
                    new { id = fetchNodeId, nodeName = "Fetch orders", stepType = "HttpAction", endpointUrl = "https://api.example.test/orders" }
                },
                edges = Array.Empty<object>()
            },
            summary = "Added the debug node.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var stub = new StubHttpMessageHandler(request =>
        {
            var raw = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var content = raw.Contains("intent router", StringComparison.Ordinal)
                ? "BUILD"
                : draft;
            return ChatResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
        });
        BuildServices(stub, out var worker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var message = await WaitForStatusAsync(agentMessageId, AiChatMessageStatus.Complete);
            Assert.NotNull(message.ResultFlowJson);

            // Both the intent router and the agent loop see the session's dialogue.
            Assert.True(stub.RequestBodies.Count >= 2, $"Expected router + build requests, got {stub.RequestBodies.Count}");
            Assert.Contains("Use the Shopify connection I saved yesterday.", stub.RequestBodies[0], StringComparison.Ordinal);
            Assert.Contains("Use the Shopify connection I saved yesterday.", stub.RequestBodies[1], StringComparison.Ordinal);
            Assert.Contains("Got it, I will use that connection.", stub.RequestBodies[1], StringComparison.Ordinal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_PrefersSavedFlowWhenUserEditedItAfterLastAgentTurn()
    {
        var providerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var savedNodeId = Guid.NewGuid();
        var agentMessageId = Guid.NewGuid();
        var staleDraft = JsonSerializer.Serialize(new
        {
            name = "Stale agent draft",
            nodes = new[] { new { id = Guid.NewGuid(), nodeName = "StaleDraftFetch", stepType = "HttpAction", endpointUrl = "https://stale.example.test/orders" } },
            edges = Array.Empty<object>()
        });
        var integrationRepository = new StubIntegrationRepository();
        integrationRepository.Seed(new IntegrationFlow
        {
            Id = flowId,
            Name = "Saved flow",
            TriggerType = TriggerType.Manual,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            Nodes = new List<IntegrationStep>
            {
                new()
                {
                    Id = savedNodeId,
                    FlowId = flowId,
                    NodeName = "SavedFlowFetch",
                    StepType = StepType.HttpAction,
                    EndpointUrl = "https://saved.example.test/orders"
                }
            }
        });
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
            context.AiChatSessions.Add(new AiChatSession
            {
                Id = sessionId,
                FlowId = flowId,
                DraftFlowJson = staleDraft
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Instruction = "Add a debug node",
                Status = AiChatMessageStatus.Complete,
                CompletedAt = DateTime.UtcNow.AddMinutes(-10)
            });
            context.AiChatMessages.Add(new AiChatMessage
            {
                Id = agentMessageId,
                SessionId = sessionId,
                Role = AiChatMessageRole.Agent,
                Instruction = "Fix the pagination on the fetch node",
                Status = AiChatMessageStatus.Queued,
                ProviderId = providerId
            });
            await context.SaveChangesAsync();
        }

        var fetchNodeId = Guid.NewGuid();
        var draft = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Saved flow fixed",
                nodes = new object[]
                {
                    new { id = fetchNodeId, nodeName = "Fetch orders", stepType = "HttpAction", endpointUrl = "https://api.example.test/orders" }
                },
                edges = Array.Empty<object>()
            },
            summary = "Fixed the pagination.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var stub = new StubHttpMessageHandler(request =>
        {
            var raw = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var content = raw.Contains("intent router", StringComparison.Ordinal)
                ? "BUILD"
                : draft;
            return ChatResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
        });
        BuildServices(stub, out var worker, integrationRepository);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var message = await WaitForStatusAsync(agentMessageId, AiChatMessageStatus.Complete);
            Assert.NotNull(message.ResultFlowJson);

            // The agent must edit the user's SAVED flow (edited after the last agent
            // turn), not the stale session snapshot.
            Assert.True(stub.RequestBodies.Count >= 2, $"Expected router + build requests, got {stub.RequestBodies.Count}");
            var buildRequest = stub.RequestBodies[1];
            Assert.Contains("SavedFlowFetch", buildRequest, StringComparison.Ordinal);
            Assert.Contains("https://saved.example.test/orders", buildRequest, StringComparison.Ordinal);
            Assert.DoesNotContain("StaleDraftFetch", buildRequest, StringComparison.Ordinal);

            using var final = CreateContext();
            var session = await final.AiChatSessions.AsNoTracking().FirstAsync(item => item.Id == sessionId);
            Assert.Equal(message.ResultFlowJson, session.DraftFlowJson);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private async Task<AiChatMessage> WaitForStatusAsync(Guid messageId, string status)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            using var context = CreateContext();
            var message = await context.AiChatMessages.AsNoTracking().FirstAsync(item => item.Id == messageId);
            if (message.Status == status) return message;
            await Task.Delay(100);
        }
        using var final = CreateContext();
        var last = await final.AiChatMessages.AsNoTracking().FirstAsync(item => item.Id == messageId);
        Assert.Fail($"Message never reached '{status}'. Last status: {last.Status}, error: {last.Error}");
        return last;
    }

    private static StubHttpMessageHandler ResearchThenDraftHandler()
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
        var researchTurns = 0;
        return new StubHttpMessageHandler(request =>
        {
            var raw = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (raw.Contains("intent router", StringComparison.Ordinal))
                return ChatResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "BUILD" } } } }));
            using var body = JsonDocument.Parse(raw);
            var usesTools = body.RootElement.TryGetProperty("tools", out _);
            var messageJson = usesTools && researchTurns++ == 0
                ? JsonSerializer.Serialize(new
                {
                    tool_calls = new[]
                    {
                        new { id = "research-1", function = new { name = "unknown_research_tool", arguments = "{}" } }
                    }
                })
                : JsonSerializer.Serialize(new { content = draft });
            return ChatResponse($"{{\"choices\":[{{\"message\":{messageJson}}}]}}");
        });
    }

    private static System.Net.Http.HttpResponseMessage ChatResponse(string body)
    {
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private ServiceProvider BuildServices(StubHttpMessageHandler handler, out AiChatWorker worker,
        StubIntegrationRepository? integrationRepository = null)
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
        services.AddSingleton<IIntegrationRepository>(integrationRepository ?? new StubIntegrationRepository());
        services.AddSingleton<IFlowTestRepository>(new StubFlowTestRepository());
        services.AddSingleton<IExecutionRepository>(new StubExecutionRepository());
        services.AddSingleton<IIntegrationCatalogRepository>(new StubIntegrationCatalogRepository());
        services.AddSingleton<ILogRepository>(new StubLogRepository());
        services.AddScoped(_ => new IPaaSContext(_options));
        services.AddScoped<AiFlowAgentTools>();
        services.AddScoped<AiFlowBuilderService>();
        var provider = services.BuildServiceProvider();
        worker = new AiChatWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHostApplicationLifetime>(),
            NullLogger<AiChatWorker>.Instance);
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



