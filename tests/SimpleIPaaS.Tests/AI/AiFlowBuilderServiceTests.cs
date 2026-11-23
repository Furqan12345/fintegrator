using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

public sealed class AiFlowBuilderServiceTests
{
    private static AiFlowAgentTools CreateTools(IHttpClientFactory clientFactory) =>
        new(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
            new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(), new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance);

    [Theory]
    [InlineData("[\"error\"]")]
    [InlineData("{\"error\":[{\"message\":\"No endpoints found\"}]}")]
    public async Task TestConnectionAsync_HandlesArrayShapedProviderError(string errorBody)
    {
        var handler = StubHttpMessageHandler.Json(errorBody, System.Net.HttpStatusCode.BadRequest);
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var exception = await Assert.ThrowsAsync<ProviderRequestException>(() => service.TestConnectionAsync(
            "OpenAICompatible",
            "https://openrouter.ai/api/v1/chat/completions",
            "test-model",
            "provider-test-key",
            CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, exception.StatusCode);
        if (errorBody.Contains("message")) Assert.Contains("No endpoints found", exception.ProviderMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_RetriesTransient503AndSucceeds()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var unavailable = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"temporarily unavailable\"}")
                };
                unavailable.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return unavailable;
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        await service.TestConnectionAsync(
            "OpenAICompatible",
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            "gemini-test",
            "provider-test-key",
            CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task TestConnectionAsync_DoesNotRetryBadRequest()
    {
        var handler = StubHttpMessageHandler.Json("{\"error\":{\"message\":\"invalid model\"}}", System.Net.HttpStatusCode.BadRequest);
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        await Assert.ThrowsAsync<ProviderRequestException>(() => service.TestConnectionAsync(
            "OpenAICompatible",
            "https://openrouter.ai/api/v1/chat/completions",
            "test-model",
            "provider-test-key",
            CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BuildAsync_RepairsNonJsonResponseOnceForGpt56()
    {
        var draftContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Repaired draft",
                nodes = new[]
                {
                    new IntegrationStepDto { Id = Guid.NewGuid(), NodeName = "Fetch orders", StepType = "HttpAction", EndpointUrl = "https://api.example.test/orders" }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Valid Shopify draft.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var responseCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            var content = ++responseCount == 1 ? "Here is the draft you requested." : draftContent;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } }
                }), System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "gpt-5.6-luna",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fetch Shopify orders and save cross references." },
            CancellationToken.None);

        Assert.True(handler.RequestBodies.Count >= 2);
        Assert.All(handler.RequestBodies, request => Assert.Contains("\"reasoning_effort\":\"none\"", request));
        Assert.Contains(result.Warnings, warning => warning.Contains("needed correction", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Repaired draft", result.Flow.Name);
    }

    [Fact]
    public async Task BuildAsync_FinalizesDraftAfterToolBudgetIsConsumed()
    {
        var draftContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Research-limited draft",
                nodes = new[]
                {
                    new IntegrationStepDto { Id = Guid.NewGuid(), NodeName = "Fetch orders", StepType = "HttpAction", EndpointUrl = "https://api.example.test/orders" }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Draft based on collected research.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var toolCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var usesTools = body.RootElement.TryGetProperty("tools", out _);
            var messageJson = usesTools
                ? JsonSerializer.Serialize(new
                {
                    tool_calls = new[]
                    {
                        new { id = $"research-{++toolCalls}", function = new { name = "unknown_research_tool", arguments = "{}" } }
                    }
                })
                : JsonSerializer.Serialize(new { content = draftContent });
            var response = $"{{\"choices\":[{{\"message\":{messageJson}}}]}}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Research and build an order integration." },
            CancellationToken.None);

        Assert.Equal(48, toolCalls);
        Assert.Contains(result.Warnings, warning => warning.Contains("tool budget", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Research-limited draft", result.Flow.Name);
    }

    [Fact]
    public async Task BuildAsync_StripsSecretsFromPrompt_AndPreservesExistingSettingsInDraft()
    {
        var existingFlowId = Guid.NewGuid();
        var existingNodeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var currentFlow = new IntegrationFlowDto
        {
            Id = existingFlowId,
            Name = "Existing flow",
            TriggerType = "Webhook",
            WebhookSecret = "do-not-send-webhook-secret",
            AllowPostReplay = true,
            PublishedVersion = 3,
            LastPublishedAt = now,
            Nodes = new List<IntegrationStepDto>
            {
                new()
                {
                    Id = existingNodeId,
                    NodeName = "Call target",
                    StepType = "HttpAction",
                    AuthType = "Bearer",
                    AuthToken = "do-not-send-node-token",
                    StepConfig = """{"apiPacket":{"headersJson":"{\"Authorization\":\"nested-private-token\"}"}}"""
                }
            }
        };
        var generatedContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "AI updated flow",
                nodes = new[]
                {
                    new IntegrationStepDto
                    {
                        Id = existingNodeId,
                        NodeName = "Call target",
                        StepType = "HttpAction",
                        EndpointUrl = "https://api.example.test/orders",
                        AuthType = "Bearer",
                        AuthToken = "model-invented-token",
                        StepConfig = """{"apiPacket":{"headersJson":"{\"Authorization\":\"[redacted]\"}"}}"""
                    },
                    new IntegrationStepDto
                    {
                        Id = Guid.NewGuid(),
                        NodeName = "Map order",
                        StepType = "Mapping",
                        MappingCode = "var state = JsonNode.Parse(FlowStateJson); var output = new JsonObject { [\"id\"] = state?[\"trigger\"]?[\"id\"]?.DeepClone() }; return output.ToJsonString();"
                    }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Updated the existing flow.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = generatedContent } } }
        });
        var handler = StubHttpMessageHandler.Json(responseJson);
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Update the target mapping", CurrentFlow = currentFlow },
            CancellationToken.None);

        Assert.Single(handler.RequestBodies);
        Assert.DoesNotContain("do-not-send-node-token", handler.RequestBodies[0]);
        Assert.DoesNotContain("do-not-send-webhook-secret", handler.RequestBodies[0]);
        Assert.DoesNotContain("nested-private-token", handler.RequestBodies[0]);
        Assert.Equal(existingFlowId, result.Flow.Id);
        Assert.Equal("Draft", result.Flow.Status);
        var generatedNode = Assert.Single(result.Flow.Nodes, node => node.Id == existingNodeId);
        var mappingNode = Assert.Single(result.Flow.Nodes, node => node.StepType == "Mapping");
        Assert.Contains("return output.ToJsonString()", mappingNode.MappingCode);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Map order: generated MappingCode did not compile", StringComparison.Ordinal));
        Assert.Equal("do-not-send-node-token", generatedNode.AuthToken);
        using var stepConfigDocument = JsonDocument.Parse(generatedNode.StepConfig);
        var headersJson = stepConfigDocument.RootElement.GetProperty("apiPacket").GetProperty("headersJson").GetString();
        using var headersDocument = JsonDocument.Parse(headersJson!);
        Assert.Equal("nested-private-token", headersDocument.RootElement.GetProperty("Authorization").GetString());
        Assert.Equal("Webhook", result.Flow.TriggerType);
        Assert.Equal("do-not-send-webhook-secret", result.Flow.WebhookSecret);
        Assert.True(result.Flow.AllowPostReplay);
        Assert.Equal(3, result.Flow.PublishedVersion);
        Assert.Equal(now, result.Flow.LastPublishedAt);
    }

    [Fact]
    public async Task BuildAsync_DoesNotRedactPaginationConfigurationFields()
    {
        var nodeId = Guid.NewGuid();
        var stepConfig = """{"pagination":{"style":"AmazonNextToken","nextTokenPath":"payload.VendorNextToken","nextTokenParameter":"VendorToken"}}""";
        var currentFlow = new IntegrationFlowDto
        {
            Id = Guid.NewGuid(),
            Name = "Amazon orders",
            TriggerType = "Manual",
            Nodes = new List<IntegrationStepDto>
            {
                new()
                {
                    Id = nodeId,
                    NodeName = "Fetch Amazon orders",
                    StepType = "HttpAction",
                    EndpointUrl = "https://api.example.test/orders",
                    HttpMethod = "GET",
                    UrlMode = "Manual",
                    AuthType = "None",
                    StepConfig = stepConfig
                }
            },
            Edges = new List<IntegrationEdgeDto>()
        };
        var generatedContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Amazon orders",
                nodes = currentFlow.Nodes,
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Reviewed the fetch node.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = generatedContent } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Remove the unused delay", CurrentFlow = currentFlow },
            CancellationToken.None);

        // These are response/request field names, not credentials. The model needs the
        // real paths to edit pagination without guessing.
        Assert.Contains("payload.VendorNextToken", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("VendorToken", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_FinalDraftSchemaIncludesCanvasPorts()
    {
        var calls = 0;
        var firstNodeId = Guid.NewGuid();
        var secondNodeId = Guid.NewGuid();
        var validDraft = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Port schema draft",
                nodes = new[]
                {
                    new IntegrationStepDto { Id = firstNodeId, NodeName = "Fetch", StepType = "Mapping" },
                    new IntegrationStepDto { Id = secondNodeId, NodeName = "Store", StepType = "Mapping" }
                },
                edges = new[]
                {
                    new IntegrationEdgeDto
                    {
                        Id = Guid.NewGuid(),
                        SourceNodeId = firstNodeId,
                        TargetNodeId = secondNodeId
                    }
                }
            },
            summary = "Valid draft.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            var content = calls == 1 ? "I need to think about this." : validDraft;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } }
                }), System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Build the flow" },
            CancellationToken.None);

        Assert.Equal(2, handler.RequestBodies.Count);
        using var request = JsonDocument.Parse(handler.RequestBodies[1]);
        var edges = request.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("flow")
            .GetProperty("properties")
            .GetProperty("edges")
            .GetProperty("items");
        var required = edges.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Contains("sourcePortId", required);
        Assert.Contains("targetPortId", required);
        Assert.True(edges.GetProperty("properties").TryGetProperty("targetPortId", out _));

        Assert.Equal("left", Assert.Single(result.Flow.Edges).TargetPortId);
        Assert.Equal("right", Assert.Single(result.Flow.Edges).SourcePortId);
    }

    [Fact]
    public async Task BuildAsync_ReturnsConversationalResultForPlainReply()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "Hi there! Tell me what flow you want to build." } } }
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "how are you" },
            CancellationToken.None,
            allowProseReply: true);

        Assert.True(result.IsConversational);
        Assert.Equal("Hi there! Tell me what flow you want to build.", result.Summary);
        Assert.Empty(result.Flow.Nodes);
        Assert.Empty(result.Flow.Edges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_ParsesDraftShapedReplyEvenWhenProseReplyIsAllowed(bool fenced)
    {
        var draftContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Explained draft",
                nodes = new[]
                {
                    new IntegrationStepDto { Id = Guid.NewGuid(), NodeName = "Fetch orders", StepType = "HttpAction", EndpointUrl = "https://api.example.test/orders" }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Valid Shopify draft.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var content = fenced ? $"```json\n{draftContent}\n```" : draftContent;
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        // The model answered a CHAT turn with the draft JSON envelope â€” the guard must
        // make it a draft, never chat text.
        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "review my draft" },
            CancellationToken.None,
            allowProseReply: true);

        Assert.False(result.IsConversational);
        Assert.Single(handler.RequestBodies);
        var node = Assert.Single(result.Flow.Nodes);
        Assert.Equal("Fetch orders", node.NodeName);
        Assert.Equal("Valid Shopify draft.", result.Summary);
    }

    [Fact]
    public async Task BuildAsync_KeepsNonDraftJsonReplyConversational()
    {
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{\"note\":\"Your draft fetches orders and stores them in the cross-reference list.\"}" } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "review my draft" },
            CancellationToken.None,
            allowProseReply: true);

        Assert.True(result.IsConversational);
        Assert.Contains("cross-reference list", result.Summary);
        Assert.Empty(result.Flow.Nodes);
    }

    [Fact]
    public async Task BuildAsync_AcceptsEnglishReplyWhenRouterMarksTurnChat()
    {
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "Your last run failed because the Shopify cursor repeated the same page. Check the dead letters for the full payload." } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        // The intent router marked this turn CHAT â€” the English explanation must reach
        // the user, not be forced into a draft repair.
        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Why did my last run fail? Check recent runs and dead letters, then propose a fix." },
            CancellationToken.None,
            allowProseReply: true);

        Assert.True(result.IsConversational);
        Assert.Contains("cursor repeated", result.Summary);
        Assert.Single(handler.RequestBodies);
        Assert.Empty(result.Flow.Nodes);
    }

    [Fact]
    public async Task BuildAsync_StillBuildsDraftWhenModelReturnsJsonOnChatTurn()
    {
        var draftContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Fixed draft",
                nodes = new[]
                {
                    new IntegrationStepDto { Id = Guid.NewGuid(), NodeName = "Fetch orders", StepType = "HttpAction", EndpointUrl = "https://api.example.test/orders" }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Adjusted the pagination.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = draftContent } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "how do I fix this error?" },
            CancellationToken.None,
            allowProseReply: true);

        Assert.False(result.IsConversational);
        Assert.Single(handler.RequestBodies);
        Assert.Equal("Adjusted the pagination.", result.Summary);
    }

    [Fact]
    public async Task BuildAsync_ForcesDraftRepairWhenRouterMarksTurnBuild()
    {
        var draftContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Repaired draft",
                nodes = new[]
                {
                    new IntegrationStepDto { Id = Guid.NewGuid(), NodeName = "Fetch orders", StepType = "HttpAction", EndpointUrl = "https://api.example.test/orders" }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Valid Shopify draft.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var responseCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            var content = ++responseCount == 1 ? "Sure, I will get right on that!" : draftContent;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } }
                }), System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        // BUILD turns keep the repair loop: a chatty first reply is nudged into the
        // JSON draft instead of silently ending with no flow.
        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Add a debug node at the end" },
            CancellationToken.None,
            allowProseReply: false);

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.False(result.IsConversational);
        Assert.Equal("Valid Shopify draft.", result.Summary);
        Assert.Contains("Return exactly one JSON object", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BUILD", true)]
    [InlineData("CHAT", false)]
    [InlineData("chat â€” the user asks a question", false)]
    [InlineData("", false)]
    public async Task IsFlowRequestAsync_RoutesOnModelAnswer(string answer, bool expected)
    {
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = answer } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var isFlowRequest = await service.IsFlowRequestAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new List<(string Role, string Content)> { ("user", "Build a Shopify sync"), ("assistant", "Draft ready to review") },
            "why did it fail?",
            CancellationToken.None);

        Assert.Equal(expected, isFlowRequest);
        Assert.Single(handler.RequestBodies);
        // The router sees the session dialogue plus the latest message, and its prompt
        // must route acknowledgments/thanks to CHAT (the "it is fixed, nice" case).
        Assert.Contains("Build a Shopify sync", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("why did it fail?", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("acknowledges, thanks, praises, or reports a result", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"tools\"", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_RestoresSecretsWhenModelRegeneratesNodeId()
    {
        var originalNodeId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var currentFlow = new IntegrationFlowDto
        {
            Name = "Shopify sync",
            TriggerType = "Manual",
            Nodes = new List<IntegrationStepDto>
            {
                new()
                {
                    Id = originalNodeId,
                    NodeName = "Fetch orders",
                    StepType = "HttpAction",
                    EndpointUrl = "https://api.example.test/orders",
                    UrlMode = "Connection",
                    AuthType = "Bearer",
                    AuthToken = "do-not-send-node-token",
                    ConnectionId = connectionId
                }
            },
            Edges = new List<IntegrationEdgeDto>()
        };
        var generatedContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Shopify sync",
                nodes = new[]
                {
                    new IntegrationStepDto
                    {
                        Id = Guid.NewGuid(),
                        NodeName = "Fetch orders",
                        StepType = "HttpAction",
                        EndpointUrl = "https://api.example.test/orders",
                        UrlMode = "Connection",
                        AuthType = "Bearer",
                        AuthToken = "model-invented-token"
                    }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Fixed the fetch node.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = generatedContent } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fix the pagination on the fetch node", CurrentFlow = currentFlow },
            CancellationToken.None);

        // The model regenerated the node ID (violating the prompt), but the unchanged
        // node keeps its identity by name â€” credentials and connection binding survive.
        var node = Assert.Single(result.Flow.Nodes);
        Assert.NotEqual(originalNodeId, node.Id);
        Assert.Equal("Fetch orders", node.NodeName);
        Assert.Equal("do-not-send-node-token", node.AuthToken);
        Assert.Equal(connectionId, node.ConnectionId);
    }

    [Fact]
    public async Task BuildAsync_RestoresPredefinedConnectionWhenModelDropsUrlMode()
    {
        var originalNodeId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var currentFlow = new IntegrationFlowDto
        {
            Name = "Shopify sync",
            TriggerType = "Manual",
            Nodes = new List<IntegrationStepDto>
            {
                new()
                {
                    Id = originalNodeId,
                    NodeName = "Fetch orders",
                    StepType = "HttpAction",
                    EndpointUrl = "https://api.example.test/orders",
                    UrlMode = "Connection",
                    AuthType = "None",
                    ConnectionId = connectionId
                }
            },
            Edges = new List<IntegrationEdgeDto>()
        };
        var generatedContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Shopify sync",
                nodes = new[]
                {
                    // The model echoed the node but dropped urlMode/endpoint entirely.
                    new IntegrationStepDto { Id = originalNodeId, NodeName = "Fetch orders", StepType = "HttpAction", UrlMode = "Manual", AuthType = "None" }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Fixed the node.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = generatedContent } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Fix the pagination on the fetch node", CurrentFlow = currentFlow },
            CancellationToken.None);

        // No hand-written URL came back, so the node never left its predefined
        // connection: the binding and endpoint survive the "use draft" click.
        var node = Assert.Single(result.Flow.Nodes);
        Assert.Equal("Connection", node.UrlMode);
        Assert.Equal(connectionId, node.ConnectionId);
        Assert.Equal("https://api.example.test/orders", node.EndpointUrl);
    }

    [Fact]
    public async Task BuildAsync_KeepsModelSelectedConnectionForRenamedNode()
    {
        var originalConnectionId = Guid.NewGuid();
        var selectedConnectionId = Guid.NewGuid();
        var currentFlow = new IntegrationFlowDto
        {
            Id = Guid.NewGuid(),
            Name = "Shopify sync",
            TriggerType = "Manual",
            Nodes = new List<IntegrationStepDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    NodeName = "FetchShopifyOrders",
                    StepType = "HttpAction",
                    EndpointUrl = "/admin/api/2023-10/graphql.json",
                    HttpMethod = "POST",
                    UrlMode = "Connection",
                    AuthType = "Custom",
                    ConnectionId = originalConnectionId
                }
            },
            Edges = new List<IntegrationEdgeDto>()
        };
        var generatedContent = JsonSerializer.Serialize(new
        {
            flow = new
            {
                name = "Shopify sync",
                nodes = new[]
                {
                    new IntegrationStepDto
                    {
                        Id = Guid.NewGuid(),
                        NodeName = "FetchOrdersGraphQL",
                        StepType = "HttpAction",
                        EndpointUrl = "/admin/api/graphql.json",
                        HttpMethod = "POST",
                        UrlMode = "Connection",
                        AuthType = "Custom",
                        ConnectionId = selectedConnectionId
                    }
                },
                edges = Array.Empty<IntegrationEdgeDto>()
            },
            summary = "Updated the fetch node.",
            warnings = Array.Empty<string>(),
            sources = Array.Empty<string>()
        });
        var handler = StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = generatedContent } } }
        }));
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            CreateTools(clientFactory),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

        var result = await service.BuildAsync(
            "OpenAICompatible",
            "http://localhost/v1/chat/completions",
            "test-model",
            "provider-test-key",
            new AiFlowBuildRequestDto { Instruction = "Use the next 50 orders from updatedAt", CurrentFlow = currentFlow },
            CancellationToken.None);

        // A renamed/new node has no old node to inherit a connection from. The id chosen
        // through list_connections must survive draft normalization and validation.
        var node = Assert.Single(result.Flow.Nodes);
        Assert.Equal("FetchOrdersGraphQL", node.NodeName);
        Assert.Equal(selectedConnectionId, node.ConnectionId);
    }
}



