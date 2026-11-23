using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

public sealed class AiProviderControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public AiProviderControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        _connection.Open();
        _options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(_connection).Options;
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task CreateProvider_EncryptsKeyAndMakesFirstProviderDefault()
    {
        using var context = CreateContext();
        var controller = CreateController(context);

        var result = await controller.CreateProvider(Request("OpenAI", apiKey: "secret-one"));

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var dto = Assert.IsType<AiProviderSettingsDto>(created.Value);
        Assert.Equal("OpenAI", dto.Name);
        Assert.True(dto.IsDefault);
        Assert.True(dto.HasApiKey);
        Assert.DoesNotContain("secret-one", JsonSerializer.Serialize(dto));

        var stored = await context.AiProviderSettings.SingleAsync();
        Assert.Equal("encrypted:secret-one", stored.EncryptedApiKey);
    }

    [Fact]
    public async Task MultipleProviders_CanChangeDefaultAndDeleteReassignsIt()
    {
        using var context = CreateContext();
        var controller = CreateController(context);

        var firstResult = Assert.IsType<CreatedAtActionResult>(
            await controller.CreateProvider(Request("First", apiKey: "first-secret")));
        var first = Assert.IsType<AiProviderSettingsDto>(firstResult.Value);
        var secondResult = Assert.IsType<CreatedAtActionResult>(
            await controller.CreateProvider(Request("Second", apiKey: "second-secret")));
        var second = Assert.IsType<AiProviderSettingsDto>(secondResult.Value);

        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);

        var setDefaultResult = await controller.SetDefaultProvider(second.Id);
        Assert.IsType<OkObjectResult>(setDefaultResult);
        Assert.False((await context.AiProviderSettings.SingleAsync(item => item.Id == first.Id)).IsDefault);
        Assert.True((await context.AiProviderSettings.SingleAsync(item => item.Id == second.Id)).IsDefault);

        var deleteResult = await controller.DeleteProvider(second.Id);
        Assert.IsType<NoContentResult>(deleteResult);
        Assert.True((await context.AiProviderSettings.SingleAsync()).IsDefault);
        Assert.Equal(first.Id, (await context.AiProviderSettings.SingleAsync()).Id);
    }

    [Fact]
    public async Task UpdateProvider_BlankApiKeyKeepsSavedKey()
    {
        using var context = CreateContext();
        var controller = CreateController(context);
        var createdResult = Assert.IsType<CreatedAtActionResult>(
            await controller.CreateProvider(Request("Private provider", apiKey: "private-secret")));
        var created = Assert.IsType<AiProviderSettingsDto>(createdResult.Value);

        var update = Request("Renamed provider");
        update.ApiKey = string.Empty;
        var updateResult = Assert.IsType<OkObjectResult>(await controller.UpdateProvider(created.Id, update));
        Assert.Equal("Renamed provider", Assert.IsType<AiProviderSettingsDto>(updateResult.Value).Name);
        Assert.Equal("encrypted:private-secret",
            (await context.AiProviderSettings.SingleAsync(item => item.Id == created.Id)).EncryptedApiKey);
    }

    [Fact]
    public async Task CreateProvider_RejectsDuplicateNamesIgnoringCase()
    {
        using var context = CreateContext();
        var controller = CreateController(context);
        await controller.CreateProvider(Request("OpenAI", apiKey: "first-secret"));

        var duplicate = await controller.CreateProvider(Request("openai", apiKey: "second-secret"));

        var badRequest = Assert.IsType<ConflictObjectResult>(duplicate);
        Assert.Contains("already exists", JsonSerializer.Serialize(badRequest.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Single(await context.AiProviderSettings.ToListAsync());
    }

    [Fact]
    public async Task CreateBuildJob_QueuesBackgroundJobForTheSelectedProvider()
    {
        using var context = CreateContext();
        var defaultProvider = Request("Default", apiKey: "default-secret");
        defaultProvider.Endpoint = "http://localhost/v1/chat/completions";
        await CreateController(context).CreateProvider(defaultProvider);

        var selectedProvider = Request("Chosen", apiKey: "chosen-secret");
        selectedProvider.Endpoint = "http://localhost/v1/chat/completions";
        selectedProvider.Model = "gpt-5.6-luna";
        var selectedResult = Assert.IsType<CreatedAtActionResult>(
            await CreateController(context).CreateProvider(selectedProvider));
        var selected = Assert.IsType<AiProviderSettingsDto>(selectedResult.Value);

        var controller = CreateController(context);
        var result = await controller.CreateBuildJob(new AiBuildJobCreateDto
        {
            ProviderId = selected.Id,
            Instruction = "Generate a flow that fetches orders from Shopify and saves the cross references."
        });

        var accepted = Assert.IsType<AcceptedAtActionResult>(result.Result);
        var dto = Assert.IsType<AiBuildJobDto>(accepted.Value);
        Assert.Equal(selected.Id, dto.ProviderId);
        Assert.Equal("gpt-5.6-luna", dto.Model);
        Assert.Equal("Queued", dto.Status);

        var stored = Assert.Single(context.AiBuildJobs.ToList());
        Assert.Equal(selected.Id, stored.ProviderId);
        Assert.Equal("Queued", stored.Status);
        Assert.Null(stored.ResultFlowJson);

        var fetched = await controller.GetBuildJob(dto.Id);
        var fetchedOk = Assert.IsType<OkObjectResult>(fetched.Result);
        Assert.IsType<AiBuildJobDto>(fetchedOk.Value);
    }
    [Fact]
    public async Task TestProvider_MapsUpstreamUnauthorizedToActionableBadGateway()
    {
        using var context = CreateContext();
        var request = Request("OpenAI", apiKey: "provider-secret");
        request.Endpoint = "http://localhost/v1/chat/completions";
        var createdResult = Assert.IsType<CreatedAtActionResult>(
            await CreateController(context).CreateProvider(request));
        var provider = Assert.IsType<AiProviderSettingsDto>(createdResult.Value);

        var handler = StubHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(), new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);
        var controller = CreateController(context, service);

        var result = await controller.TestProviderById(provider.Id);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, response.StatusCode);
        Assert.Contains("rejected the API key", JsonSerializer.Serialize(response.Value));
    }
    [Fact]
    public async Task TestProvider_RetriesWithCompletionTokenLimitWhenAzureAliasRejectsMaxTokens()
    {
        using var context = CreateContext();
        var request = Request("Azure alias", apiKey: "provider-secret");
        request.ProviderType = "AzureOpenAI";
        request.Endpoint = "http://localhost/openai/v1/chat/completions";
        request.Model = "shopify-orders-prod";
        var createdResult = Assert.IsType<CreatedAtActionResult>(
            await CreateController(context).CreateProvider(request));
        var provider = Assert.IsType<AiProviderSettingsDto>(createdResult.Value);
        var handler = new StubHttpMessageHandler(httpRequest =>
        {
            var body = JsonDocument.Parse(httpRequest.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var usesLegacyLimit = body.RootElement.TryGetProperty("max_tokens", out _);
            return new HttpResponseMessage(usesLegacyLimit ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(usesLegacyLimit
                    ? "{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\"}}"
                    : "{}", System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(), new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);
        var controller = CreateController(context, service);

        var result = await controller.TestProviderById(provider.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("\"max_tokens\":4096", handler.RequestBodies[0]);
        Assert.DoesNotContain("\"max_completion_tokens\"", handler.RequestBodies[0]);
        Assert.Contains("\"max_completion_tokens\":4096", handler.RequestBodies[1]);
        Assert.DoesNotContain("\"max_tokens\"", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task TestProvider_BadRequestIncludesSanitizedAzureDetailsAndUsesGpt56Tokens()
    {
        using var context = CreateContext();
        const string apiKey = "provider-secret-not-for-response";
        var request = Request("Azure GPT", apiKey: apiKey);
        request.ProviderType = "AzureOpenAI";
        request.Endpoint = "http://localhost/openai/v1/chat/completions";
        request.Model = "gpt-5.6-luna";
        var createdResult = Assert.IsType<CreatedAtActionResult>(
            await CreateController(context).CreateProvider(request));
        var provider = Assert.IsType<AiProviderSettingsDto>(createdResult.Value);

        var providerError = JsonSerializer.Serialize(new
        {
            error = new { message = $"Unknown model deployment. {apiKey}" }
        });
        var handler = StubHttpMessageHandler.Json(providerError, HttpStatusCode.BadRequest);
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = new AiFlowBuilderService(
            clientFactory,
            new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(), new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);
        var controller = CreateController(context, service);

        var result = await controller.TestProviderById(provider.Id);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, response.StatusCode);
        var responseBody = JsonSerializer.Serialize(response.Value);
        Assert.Contains("Unknown model deployment", responseBody);
        Assert.DoesNotContain(apiKey, responseBody);

        var requestBody = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"max_completion_tokens\":4096", requestBody);
        Assert.DoesNotContain("\"max_tokens\"", requestBody);
        Assert.DoesNotContain("\"reasoning_effort\"", requestBody);
        Assert.True(handler.Requests[0].Headers.TryGetValues("api-key", out var apiKeyHeader));
        Assert.Equal(apiKey, Assert.Single(apiKeyHeader));
    }
    private IPaaSContext CreateContext() => new(_options);

    private static AiFlowBuilderController CreateController(IPaaSContext context, AiFlowBuilderService? service = null, IEncryptionService? encryption = null)
    {
        if (service == null)
        {
            var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("No provider request expected."));
            var clientFactory = new StubHttpClientFactory(handler);
            service = new AiFlowBuilderService(
                clientFactory,
                new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                    new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(), new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
                new StubHostEnvironment(Environments.Development),
                NullLogger<AiFlowBuilderService>.Instance);
        }
        var controller = new AiFlowBuilderController(context, encryption ?? new TestEncryptionService(), service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    [Fact]
    public async Task TestProvider_UndecryptableStoredKeyReturnsConflictInsteadOfServerError()
    {
        using var context = CreateContext();
        var createdResult = Assert.IsType<CreatedAtActionResult>(
            await CreateController(context).CreateProvider(Request("Rotated key", apiKey: "provider-secret")));
        var provider = Assert.IsType<AiProviderSettingsDto>(createdResult.Value);

        // Mimics a rotated Encryption:Key: the stored ciphertext can no longer be decrypted.
        var controller = CreateController(context, encryption: new ThrowingEncryptionService());

        var result = await controller.TestProviderById(provider.Id);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, response.StatusCode);
        var body = JsonSerializer.Serialize(response.Value);
        Assert.Contains("cannot be decrypted", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-enter the API key", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/v1/chat/completions")]                        // wrong scheme
    [InlineData("https://user:secret@127.0.0.1/v1/chat/completions")]          // credentials in the URL
    [InlineData("https://127.0.0.1/v1/chat/completions#fragment")]             // fragment
    [InlineData("not a url")]
    public async Task CreateProvider_RejectsEndpointsOutsideTheRuleAndStatesIt(string endpoint)
    {
        using var context = CreateContext();
        var request = Request("Bad endpoint", apiKey: "provider-secret");
        request.Endpoint = endpoint;

        var result = await CreateController(context).CreateProvider(request);

        var response = Assert.IsType<BadRequestObjectResult>(result);
        var body = JsonSerializer.Serialize(response.Value);
        // The message has to state the rule the operator must satisfy, including the port and
        // self-hosted/HTTPS parts â€” not just "ending in /chat/completions".
        Assert.Contains("Any port is allowed", body);
        Assert.Contains("localhost", body);
        Assert.Contains("private-network", body);
        Assert.Contains("HTTPS", body);
        Assert.DoesNotContain("secret", body);
        Assert.Empty(context.AiProviderSettings);
    }

    [Fact]
    public async Task CreateProvider_AcceptsSelfHostedEndpointOnANonDefaultPort()
    {
        using var context = CreateContext();
        var request = Request("Ollama", apiKey: "provider-secret");
        request.Endpoint = "http://127.0.0.1:11434/v1/chat/completions";

        var result = await CreateController(context).CreateProvider(request);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var dto = Assert.IsType<AiProviderSettingsDto>(created.Value);
        // The URL the operator pasted is stored as the API root the mode derives from.
        Assert.Equal("http://127.0.0.1:11434/v1", dto.Endpoint);
        Assert.Equal(AiApiModes.ChatCompletions, dto.ApiMode);
    }

    [Fact]
    public async Task CreateProvider_DefaultsOpenAiEndpointToResponsesAndStoresTheApiRoot()
    {
        using var context = CreateContext();
        var request = Request("OpenAI", apiKey: "provider-secret");
        request.Endpoint = "https://api.openai.com/v1/chat/completions";

        var created = Assert.IsType<CreatedAtActionResult>(await CreateController(context).CreateProvider(request));

        var dto = Assert.IsType<AiProviderSettingsDto>(created.Value);
        Assert.Equal(AiApiModes.Responses, dto.ApiMode);
        Assert.Equal("https://api.openai.com/v1", dto.Endpoint);
        Assert.False(dto.EnableWebSearch);
        Assert.Null(dto.ReasoningEffort);
    }

    [Fact]
    public async Task CreateProvider_DefaultsSelfHostedAndAzureEndpointsToChatCompletions()
    {
        using var context = CreateContext();
        var controller = CreateController(context);

        var selfHosted = Request("LiteLLM", apiKey: "provider-secret");
        selfHosted.Endpoint = "http://127.0.0.1:4000/v1";
        var selfHostedDto = Assert.IsType<AiProviderSettingsDto>(
            Assert.IsType<CreatedAtActionResult>(await controller.CreateProvider(selfHosted)).Value);
        Assert.Equal(AiApiModes.ChatCompletions, selfHostedDto.ApiMode);

        // Azure stays operator-selectable and only Answers the deployment path style.
        var azure = Request("Azure", apiKey: "provider-secret");
        azure.ProviderType = "AzureOpenAI";
        azure.Endpoint = "https://res.openai.azure.com/openai/deployments/orders/chat/completions?api-version=2024-10-21";
        var azureDto = Assert.IsType<AiProviderSettingsDto>(
            Assert.IsType<CreatedAtActionResult>(await controller.CreateProvider(azure)).Value);
        Assert.Equal(AiApiModes.ChatCompletions, azureDto.ApiMode);
        Assert.Equal("https://res.openai.azure.com/openai/deployments/orders?api-version=2024-10-21", azureDto.Endpoint);
    }

    [Fact]
    public async Task CreateProvider_AcceptsAnExplicitResponsesModeWithReasoningAndWebSearch()
    {
        using var context = CreateContext();
        var request = Request("Responses", apiKey: "provider-secret");
        request.Endpoint = "https://api.openai.com/v1/responses";
        request.ApiMode = "responses";
        request.ReasoningEffort = "HIGH";
        request.EnableWebSearch = true;

        var created = Assert.IsType<CreatedAtActionResult>(await CreateController(context).CreateProvider(request));

        var dto = Assert.IsType<AiProviderSettingsDto>(created.Value);
        Assert.Equal(AiApiModes.Responses, dto.ApiMode);
        Assert.Equal("high", dto.ReasoningEffort);
        Assert.True(dto.EnableWebSearch);
        // An explicit /responses URL and the mode agree; the stored value is the shared root.
        Assert.Equal("https://api.openai.com/v1", dto.Endpoint);
    }

    [Fact]
    public async Task CreateProvider_AzureRemainsOperatorSelectableForResponses()
    {
        using var context = CreateContext();
        // Azure defaults to Chat Completions, but the operator may opt into Responses; whether
        // the chosen api-version/deployment supports it is confirmed by the provider itself.
        var request = Request("Azure responses", apiKey: "provider-secret");
        request.ProviderType = "AzureOpenAI";
        request.Endpoint = "https://res.openai.azure.com/openai/deployments/orders/chat/completions?api-version=2025-04-01-preview";
        request.ApiMode = AiApiModes.Responses;
        request.ReasoningEffort = "medium";

        var created = Assert.IsType<CreatedAtActionResult>(await CreateController(context).CreateProvider(request));

        var dto = Assert.IsType<AiProviderSettingsDto>(created.Value);
        Assert.Equal(AiApiModes.Responses, dto.ApiMode);
        Assert.Equal("https://res.openai.azure.com/openai/deployments/orders?api-version=2025-04-01-preview", dto.Endpoint);
        Assert.Equal("https://res.openai.azure.com/openai/deployments/orders/responses?api-version=2025-04-01-preview",
            AiProviderEndpoints.DeriveRequestUrl(dto.Endpoint, dto.ApiMode));
    }

    [Fact]
    public async Task CreateProvider_AcceptsResponsesUrlInChatCompletionsModeByNormalizingSilently()
    {
        using var context = CreateContext();
        var request = Request("Chat mode", apiKey: "provider-secret");
        request.Endpoint = "https://api.openai.com/v1/responses";
        request.ApiMode = AiApiModes.ChatCompletions;

        var created = Assert.IsType<CreatedAtActionResult>(await CreateController(context).CreateProvider(request));

        var dto = Assert.IsType<AiProviderSettingsDto>(created.Value);
        Assert.Equal(AiApiModes.ChatCompletions, dto.ApiMode);
        Assert.Equal("https://api.openai.com/v1", dto.Endpoint);
        Assert.Equal("https://api.openai.com/v1/chat/completions", AiProviderEndpoints.DeriveRequestUrl(dto.Endpoint, dto.ApiMode));
    }

    [Fact]
    public async Task UpdateProvider_BlankModeKeepsStoredModeAndBlankKeyKeepsTheSecret()
    {
        using var context = CreateContext();
        var controller = CreateController(context);
        var create = Request("Responses", apiKey: "provider-secret");
        create.Endpoint = "http://127.0.0.1:4000/v1";
        create.ApiMode = AiApiModes.Responses;
        create.ReasoningEffort = "medium";
        create.EnableWebSearch = true;
        var created = Assert.IsType<AiProviderSettingsDto>(
            Assert.IsType<CreatedAtActionResult>(await controller.CreateProvider(create)).Value);

        // An old client (or the compatibility route) sends no mode and no web-search flag.
        var update = Request("Renamed", apiKey: string.Empty);
        update.ApiMode = string.Empty;
        update.Endpoint = "http://127.0.0.1:4000/v1/chat/completions";
        update.ReasoningEffort = "low";
        var updated = Assert.IsType<AiProviderSettingsDto>(
            Assert.IsType<OkObjectResult>(await controller.UpdateProvider(created.Id, update)).Value);

        Assert.Equal(AiApiModes.Responses, updated.ApiMode);
        Assert.Equal("low", updated.ReasoningEffort);
        Assert.True(updated.EnableWebSearch); // not sent -> kept
        Assert.Equal("http://127.0.0.1:4000/v1", updated.Endpoint);
        Assert.Equal("encrypted:provider-secret",
            (await context.AiProviderSettings.SingleAsync(item => item.Id == created.Id)).EncryptedApiKey);

        // An explicit false clears it.
        update.EnableWebSearch = false;
        var cleared = Assert.IsType<AiProviderSettingsDto>(
            Assert.IsType<OkObjectResult>(await controller.UpdateProvider(created.Id, update)).Value);
        Assert.False(cleared.EnableWebSearch);
    }

    [Fact]
    public async Task TestProvider_OldStoredChatCompletionsRowKeepsPostingToTheStoredUrl()
    {
        using var context = CreateContext();
        // Exactly what a row created before the API mode existed looks like: a full
        // chat-completions URL and no mode (the column default backfills ChatCompletions).
        var legacy = new AiProviderSettings
        {
            Name = "Legacy provider",
            IsDefault = true,
            ProviderType = "OpenAICompatible",
            Endpoint = "http://localhost/v1/chat/completions",
            Model = "legacy-model",
            EncryptedApiKey = "encrypted:legacy-secret",
            UpdatedAt = DateTime.UtcNow
        };
        context.AiProviderSettings.Add(legacy);
        await context.SaveChangesAsync();

        var handler = StubHttpMessageHandler.Json("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
        using var clientFactory = new StubHttpClientFactory(handler);
        var controller = CreateController(context, CreateService(clientFactory));

        var result = await controller.TestProviderById(legacy.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("http://localhost/v1/chat/completions", Assert.Single(handler.Requests).RequestUri!.ToString());
        Assert.Equal("http://localhost/v1/chat/completions",
            (await context.AiProviderSettings.SingleAsync(item => item.Id == legacy.Id)).Endpoint);
    }

    [Fact]
    public async Task TestProvider_ResponsesModePostsToTheResponsesEndpointWithReasoningAndWebSearch()
    {
        using var context = CreateContext();
        var request = Request("Responses", apiKey: "provider-secret");
        request.Endpoint = "http://127.0.0.1:4000/v1";
        request.ApiMode = AiApiModes.Responses;
        request.ReasoningEffort = "minimal";
        request.EnableWebSearch = true;
        var created = Assert.IsType<AiProviderSettingsDto>(
            Assert.IsType<CreatedAtActionResult>(await CreateController(context).CreateProvider(request)).Value);

        var handler = StubHttpMessageHandler.Json(
            "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}]}");
        using var clientFactory = new StubHttpClientFactory(handler);
        var controller = CreateController(context, CreateService(clientFactory));

        var result = await controller.TestProviderById(created.Id);

        Assert.IsType<OkObjectResult>(result);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:4000/v1/responses", sent.RequestUri!.ToString());
        Assert.True(sent.Headers.TryGetValues("Authorization", out var authorization));
        Assert.Equal("Bearer provider-secret", Assert.Single(authorization));
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"store\":false", body);
        Assert.Contains("\"web_search\"", body);
        Assert.Contains("\"reasoning\":{\"effort\":\"minimal\"}", body);
        Assert.Contains("input_text", body);
    }

    private static AiFlowBuilderService CreateService(IHttpClientFactory clientFactory) =>
        new(clientFactory,
            new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(), new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
            new StubHostEnvironment(Environments.Development),
            NullLogger<AiFlowBuilderService>.Instance);

    private static AiProviderSettingsUpdateDto Request(string name, string apiKey = "") => new()
    {
        Name = name,
        ProviderType = "OpenAICompatible",
        Endpoint = "https://api.openai.com/v1/chat/completions",
        Model = "gpt-test",
        ApiKey = apiKey
    };

    public void Dispose() => _connection.Dispose();

    private sealed class TestEncryptionService : IEncryptionService
    {
        public Task<string> EncryptAsync(string plainText) => Task.FromResult("encrypted:" + plainText);
        public Task<string> DecryptAsync(string cipherText) => Task.FromResult(cipherText["encrypted:".Length..]);
    }

    private sealed class ThrowingEncryptionService : IEncryptionService
    {
        public Task<string> EncryptAsync(string plainText) => Task.FromResult("encrypted:" + plainText);
        public Task<string> DecryptAsync(string cipherText) =>
            throw new System.Security.Cryptography.CryptographicException("InvalidTag: the key was rotated.");
    }
}



