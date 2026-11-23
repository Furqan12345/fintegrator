using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Engine;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Infrastructure.Services.Security;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Performance;

// HTTP card responses are buffered in memory. These tests pin the two memory rules: a body is decoded once
// (not once per consumer), and a body larger than the configured cap fails the call instead of exhausting RAM.
public sealed class TransportMemoryTests
{
    private sealed class FixedBodyHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FixedBodyHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class NoAuthHandler : IAuthenticationHandler
    {
        public Task AuthenticateAsync(HttpRequestMessage request, string configJson) => Task.CompletedTask;
        public Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context) => Task.CompletedTask;
    }

    private sealed class NoAuthFactory : IAuthenticationHandlerFactory
    {
        public IAuthenticationHandler GetHandler(AuthType authType) => new NoAuthHandler();
    }

    private sealed class PassThroughEncryption : IEncryptionService
    {
        public Task<string> EncryptAsync(string plainText) => Task.FromResult(plainText);
        public Task<string> DecryptAsync(string cipherText) => Task.FromResult(cipherText);
    }

    private static TransportEngine CreateTransport(string body, long maxBufferBytes = int.MaxValue)
    {
        var client = new HttpClient(new FixedBodyHandler(body)) { MaxResponseContentBufferSize = maxBufferBytes };
        return new TransportEngine(
            new SingleClientFactory(client),
            new StubConnectionRepository(),
            new NoAuthFactory(),
            new PassThroughEncryption(),
            new OutboundUrlPolicy(Options.Create(new OutboundOptions { AllowPrivateNetworks = true })),
            NullLogger<TransportEngine>.Instance);
    }

    private static IntegrationStep Step() => new()
    {
        StepType = StepType.HttpAction,
        HttpMethod = "GET",
        EndpointUrl = "http://127.0.0.1:9/data",
        AuthType = AuthType.None,
        NodeName = "Fetch"
    };

    [Fact]
    public async Task ResponseBody_IsDecodedOnce_AndSharedByTheResponseAndItsAttempt()
    {
        var body = "{\"data\":\"" + new string('x', 200_000) + "\"}";
        var transport = CreateTransport(body);

        var response = await transport.DispatchAsync(Step(), payload: null);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(body, response.Response);
        // Same string instance: the second ReadAsStringAsync that used to duplicate the body is gone.
        Assert.Same(response.Response, Assert.Single(response.Attempts).Response);
    }

    [Fact]
    public async Task ResponseLargerThanTheConfiguredCap_FailsTheCall_InsteadOfBufferingIt()
    {
        var body = new string('y', 50_000);
        var transport = CreateTransport(body, maxBufferBytes: 4_000);

        var response = await transport.DispatchAsync(Step(), payload: null);

        Assert.Equal(0, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(response.Error));
        Assert.Contains("buffer", response.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResponseWithinTheCap_IsReturnedNormally()
    {
        var body = new string('z', 3_000);
        var transport = CreateTransport(body, maxBufferBytes: 4_000);

        var response = await transport.DispatchAsync(Step(), payload: null);

        Assert.Equal(200, response.StatusCode);
        Assert.Null(response.Error);
        Assert.Equal(body, response.Response);
    }

    [Fact]
    public void OutboundClient_UsesTheConfiguredResponseCap_WithA64MbDefault()
    {
        Assert.Equal(64L * 1024 * 1024, new OutboundOptions().MaxResponseBytes);

        long CapFor(string? configured)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:DefaultConnection"] = "Data Source=TransportCap;Mode=Memory;Cache=Shared;Pooling=False",
                ["Encryption:Key"] = "0123456789abcdef0123456789abcdef"
            };
            if (configured != null) settings["Outbound:MaxResponseBytes"] = configured;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(Environments.Development));
            services.AddEngineServices(configuration, new StubHostEnvironment(Environments.Development));
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IHttpClientFactory>().CreateClient("Outbound").MaxResponseContentBufferSize;
        }

        Assert.Equal(64L * 1024 * 1024, CapFor(null));
        Assert.Equal(1_000_000L, CapFor("1000000"));
    }
}
