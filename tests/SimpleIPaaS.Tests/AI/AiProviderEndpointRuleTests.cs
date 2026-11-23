using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.AI;

// The endpoint rule used to reject every port except 80/443, which made self-hosted
// OpenAI-compatible servers (Ollama :11434, LM Studio :1234, vLLM :8000, LiteLLM :4000)
// impossible to configure — while the error text never mentioned the port rule.
public sealed class AiProviderEndpointRuleTests
{
    private static AiFlowBuilderService CreateService(StubHttpClientFactory clientFactory, string environment)
    {
        return new AiFlowBuilderService(
            clientFactory,
            new AiFlowAgentTools(clientFactory, new StubCrossReferenceRepository(), new StubConnectionRepository(),
                new StubIntegrationRepository(), new StubFlowTestRepository(), new StubExecutionRepository(),
                new StubIntegrationCatalogRepository(), new StubLogRepository(), NullLogger<AiFlowAgentTools>.Instance),
            new StubHostEnvironment(environment),
            NullLogger<AiFlowBuilderService>.Instance);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9310/v1/chat/completions")]      // local fake OpenAI-compatible server
    [InlineData("http://localhost:11434/v1/chat/completions")]     // Ollama
    [InlineData("http://10.0.0.7:8000/v1/chat/completions")]       // vLLM on a private address
    [InlineData("http://192.168.1.20:1234/v1/chat/completions")]   // LM Studio on a private address
    [InlineData("https://192.168.1.30:9443/v1/chat/completions")]  // private address over https
    [InlineData("https://203.0.113.9:8443/v1/chat/completions")]   // public host, non-default port
    public async Task TestConnection_AcceptsSelfHostedAndNonDefaultPorts(string endpoint)
    {
        var handler = StubHttpMessageHandler.Json("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
        using var clientFactory = new StubHttpClientFactory(handler);
        var service = CreateService(clientFactory, Environments.Development);

        var result = await service.TestConnectionAsync("OpenAICompatible", endpoint, "gpt-test", "test-key", CancellationToken.None);

        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/v1/chat/completions")]
    [InlineData("http://203.0.113.9:9000/v1/chat/completions")]                 // public host over plain HTTP
    [InlineData("https://127.0.0.1/v1/completions")]                            // wrong path
    [InlineData("https://user:secret@127.0.0.1/v1/chat/completions")]           // credentials in the URL
    [InlineData("https://127.0.0.1/v1/chat/completions#fragment")]              // fragment
    [InlineData("https://169.254.169.254/latest/meta-data/chat/completions")]   // cloud metadata address
    public async Task TestConnection_RejectsEndpointsOutsideTheRule(string endpoint)
    {
        using var clientFactory = StubHttpClientFactory.Unreachable();
        var service = CreateService(clientFactory, Environments.Production);

        var exception = await Assert.ThrowsAsync<AiFlowBuilderException>(() =>
            service.TestConnectionAsync("OpenAICompatible", endpoint, "gpt-test", "test-key", CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Fact]
    public void EndpointRuleStatesPortsAndSelfHostedAddressesExplicitly()
    {
        Assert.Contains("Any port is allowed", AiFlowBuilderService.ProviderEndpointRule);
        Assert.Contains("localhost", AiFlowBuilderService.ProviderEndpointRule);
        Assert.Contains("private-network", AiFlowBuilderService.ProviderEndpointRule);
        Assert.Contains("HTTPS", AiFlowBuilderService.ProviderEndpointRule);
    }

    [Fact]
    public async Task ValidatePublicProviderEndpoint_AcceptsLoopbackHttpOutsideDevelopmentToo()
    {
        using var clientFactory = StubHttpClientFactory.Unreachable();
        var service = CreateService(clientFactory, Environments.Production);

        var uri = await service.ValidatePublicProviderEndpointAsync(
            "http://127.0.0.1:9310/v1/chat/completions", CancellationToken.None);

        Assert.Equal(9310, uri.Port);
        Assert.Equal("http", uri.Scheme);
    }

    [Fact]
    public async Task ValidatePublicProviderEndpoint_RejectsPublicHttp()
    {
        using var clientFactory = StubHttpClientFactory.Unreachable();
        var service = CreateService(clientFactory, Environments.Production);

        var exception = await Assert.ThrowsAsync<AiFlowBuilderException>(() => service.ValidatePublicProviderEndpointAsync(
            "http://203.0.113.9:9000/v1/chat/completions", CancellationToken.None));

        Assert.Contains("HTTPS", exception.Message);
    }
}
