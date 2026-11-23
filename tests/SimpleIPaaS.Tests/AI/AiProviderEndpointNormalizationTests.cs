using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

// Operators paste whatever their provider shows them — the API root, a full
// /chat/completions URL, a /responses URL, an Azure deployment path with its api-version
// query, or a root with no /v1 at all. All of them must reduce to the same stored API root,
// and the request URL must be derived from that root per API mode.
public sealed class AiProviderEndpointNormalizationTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/chat/completions/", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/responses", "https://api.openai.com/v1")]
    [InlineData("https://api.openai.com", "https://api.openai.com")]
    [InlineData("https://api.openai.com/", "https://api.openai.com")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:4000/v1/responses", "http://127.0.0.1:4000/v1")]                 // LiteLLM
    [InlineData("http://192.168.1.20:1234/chat/completions", "http://192.168.1.20:1234")]          // LM Studio, no /v1
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "https://generativelanguage.googleapis.com/v1beta/openai")]
    [InlineData("https://res.openai.azure.com/openai/deployments/orders/chat/completions?api-version=2024-10-21",
        "https://res.openai.azure.com/openai/deployments/orders?api-version=2024-10-21")]
    [InlineData("https://res.openai.azure.com/openai/deployments/orders?api-version=2024-10-21",
        "https://res.openai.azure.com/openai/deployments/orders?api-version=2024-10-21")]
    public void NormalizeRoot_StripsTheEndpointSuffixAndKeepsTheQuery(string endpoint, string expected)
    {
        Assert.Equal(expected, AiProviderEndpoints.NormalizeRoot(endpoint));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "Responses", "https://api.openai.com/v1/responses")]
    [InlineData("https://api.openai.com/v1", "ChatCompletions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/chat/completions", "Responses", "https://api.openai.com/v1/responses")]
    [InlineData("https://api.openai.com/v1/responses", "ChatCompletions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "Responses", "https://api.openai.com/v1/responses")]
    [InlineData("https://openrouter.ai/api/v1", "ChatCompletions", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://litellm.example.test", "ChatCompletions", "https://litellm.example.test/chat/completions")]
    [InlineData("https://res.openai.azure.com/openai/deployments/orders?api-version=2024-10-21", "ChatCompletions",
        "https://res.openai.azure.com/openai/deployments/orders/chat/completions?api-version=2024-10-21")]
    [InlineData("https://res.openai.azure.com/openai/deployments/orders/chat/completions?api-version=2024-10-21", "Responses",
        "https://res.openai.azure.com/openai/deployments/orders/responses?api-version=2024-10-21")]
    public void DeriveRequestUrl_AppendsTheModeSuffixAndKeepsAzureQuery(string endpoint, string apiMode, string expected)
    {
        Assert.Equal(expected, AiProviderEndpoints.DeriveRequestUrl(endpoint, apiMode));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/responses")]
    [InlineData("https://litellm.example.test")]                              // a plain host is a valid root
    [InlineData("http://127.0.0.1:11434/v1")]                                 // self-hosted root
    [InlineData("http://localhost:9310/chat/completions")]
    [InlineData("https://res.openai.azure.com/openai/deployments/orders?api-version=2024-10-21")]
    public void Validate_AcceptsRootsAndFullEndpoints(string endpoint)
    {
        Assert.Null(AiProviderEndpoints.Validate(endpoint));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1/v1/chat/completions")]
    [InlineData("https://user:secret@127.0.0.1/v1/chat/completions")]
    [InlineData("https://127.0.0.1/v1/chat/completions#fragment")]
    [InlineData("https://127.0.0.1/v1/completions")]                          // legacy completions family
    public void Validate_RejectsValuesOutsideTheRuleAndStatesIt(string endpoint)
    {
        var rejection = AiProviderEndpoints.Validate(endpoint);

        Assert.NotNull(rejection);
        Assert.Contains("Any port is allowed", rejection);
        Assert.Contains("localhost", rejection);
        Assert.Contains("private-network", rejection);
        Assert.Contains("HTTPS", rejection);
        Assert.DoesNotContain("secret", rejection);
    }

    [Fact]
    public void Validate_NormalizesAnExplicitResponsesUrlInChatCompletionsModeSilently()
    {
        // Stripping the suffix is lossless, so a mode/URL mismatch never breaks: the value is
        // accepted and stored as the same root either way (no operand is mode-specific here).
        Assert.Null(AiProviderEndpoints.Validate("https://api.openai.com/v1/responses", AiApiModes.ChatCompletions));
        Assert.Null(AiProviderEndpoints.Validate("https://api.openai.com/v1/chat/completions", AiApiModes.Responses));
        Assert.Equal("https://api.openai.com/v1",
            AiProviderEndpoints.NormalizeRoot("https://api.openai.com/v1/responses"));
    }

    [Theory]
    [InlineData("OpenAICompatible", "https://api.openai.com/v1/chat/completions", "Responses")]   // OpenAI's own host
    [InlineData("OpenAICompatible", "https://api.openai.com/v1", "Responses")]
    [InlineData("OpenAICompatible", "http://127.0.0.1:11434/v1/chat/completions", "ChatCompletions")]
    [InlineData("OpenAICompatible", "https://openrouter.ai/api/v1", "ChatCompletions")]
    [InlineData("AzureOpenAI", "https://res.openai.azure.com/openai/deployments/orders?api-version=2024-10-21", "ChatCompletions")]
    [InlineData("OpenAICompatible", "", "ChatCompletions")]
    public void DefaultFor_PicksTheModePerProviderTypeAndHost(string providerType, string endpoint, string expected)
    {
        Assert.Equal(expected, AiApiModes.DefaultFor(providerType, endpoint));
    }

    [Theory]
    [InlineData("minimal", "minimal")]
    [InlineData("HIGH", "high")]
    [InlineData(" medium ", "medium")]
    [InlineData("turbo", null)]           // unknown effort values never reach the provider
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReasoningEffort_NormalizesToTheSupportedValues(string? value, string? expected)
    {
        Assert.Equal(expected, AiReasoningEfforts.Normalize(value));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/responses")]
    [InlineData("http://127.0.0.1:4000/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1")]
    public async Task ValidatePublicProviderEndpoint_AcceptsEverySupportedFormAndReturnsTheRoot(string endpoint)
    {
        using var clientFactory = StubHttpClientFactory.Unreachable();
        var service = CreateService(clientFactory, Environments.Development);

        var uri = await service.ValidatePublicProviderEndpointAsync(endpoint, CancellationToken.None);

        Assert.Equal(AiProviderEndpoints.NormalizeRoot(endpoint), uri.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("https://127.0.0.1/v1/completions")]
    [InlineData("ftp://127.0.0.1/v1/chat/completions")]
    [InlineData("https://user:secret@127.0.0.1/v1/chat/completions")]
    [InlineData("https://127.0.0.1/v1/chat/completions#fragment")]
    public async Task ValidatePublicProviderEndpoint_RejectsValuesOutsideTheRule(string endpoint)
    {
        using var clientFactory = StubHttpClientFactory.Unreachable();
        var service = CreateService(clientFactory, Environments.Production);

        var exception = await Assert.ThrowsAsync<AiFlowBuilderException>(() =>
            service.ValidatePublicProviderEndpointAsync(endpoint, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.DoesNotContain("secret", exception.Message);
    }

    private static AiFlowBuilderService CreateService(StubHttpClientFactory clientFactory, string environment) =>
        new(clientFactory,
            new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(),
                new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
            new StubHostEnvironment(environment),
            NullLogger<AiFlowBuilderService>.Instance);
}
