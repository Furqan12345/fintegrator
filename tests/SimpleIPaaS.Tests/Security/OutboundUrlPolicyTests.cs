using Microsoft.Extensions.Options;
using SimpleIPaaS.Infrastructure.Services.Security;

namespace SimpleIPaaS.Tests.Security;

public class OutboundUrlPolicyTests
{
    private static OutboundUrlPolicy Create(bool allowPrivate = false, params string[] allowedHosts) =>
        new(Options.Create(new OutboundOptions
        {
            AllowPrivateNetworks = allowPrivate,
            AllowedHosts = allowedHosts
        }));

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.0.5/")]
    [InlineData("http://192.168.1.5/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fe80::1]/")]
    public async Task BlocksPrivateAndMetadataAddresses(string url)
    {
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => Create().ValidateAsync(url));
    }

    [Theory]
    [InlineData("ftp://8.8.8.8/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public async Task BlocksNonHttpUrls(string url)
    {
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => Create().ValidateAsync(url));
    }

    [Fact]
    public async Task AllowsPublicIpLiteral()
    {
        var uri = await Create().ValidateAsync("https://8.8.8.8/path");
        Assert.Equal("8.8.8.8", uri.Host);
    }

    [Fact]
    public async Task AllowsPrivateNetworksOnlyWhenConfigured()
    {
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => Create().ValidateAsync("http://10.0.0.5/"));

        var uri = await Create(allowPrivate: true).ValidateAsync("http://10.0.0.5/");
        Assert.Equal("10.0.0.5", uri.Host);
    }

    [Fact]
    public async Task EnforcesTheHostAllowList()
    {
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() =>
            Create(allowedHosts: "api.example.com").ValidateAsync("https://8.8.8.8/"));

        var uri = await Create(allowedHosts: "8.8.8.8").ValidateAsync("https://8.8.8.8/");
        Assert.Equal("8.8.8.8", uri.Host);
    }
}
