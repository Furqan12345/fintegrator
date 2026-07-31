using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Infrastructure.Services.Security;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Security;

public class EncryptionServiceTests
{
    private const string ValidKey = "0123456789abcdef0123456789abcdef";

    private static EncryptionService Create(string? key = ValidKey, string environment = "Production")
    {
        var settings = new Dictionary<string, string?>();
        if (key != null) settings["Encryption:Key"] = key;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new EncryptionService(configuration, new StubHostEnvironment(environment), NullLogger<EncryptionService>.Instance);
    }

    [Fact]
    public async Task EncryptThenDecrypt_ReturnsTheOriginalValue()
    {
        var service = Create();
        const string plainText = "{\"clientSecret\":\"s3cr3t\",\"scope\":\"orders.read\"}";

        var cipherText = await service.EncryptAsync(plainText);

        Assert.Equal(plainText, await service.DecryptAsync(cipherText));
    }

    [Fact]
    public async Task EncryptThenDecrypt_RoundTripsNonAsciiText()
    {
        var service = Create();
        const string plainText = "clé-secrète ✅ 你好";

        var cipherText = await service.EncryptAsync(plainText);

        Assert.Equal(plainText, await service.DecryptAsync(cipherText));
    }

    [Fact]
    public async Task EncryptAsync_DoesNotReturnThePlaintext()
    {
        var service = Create();
        const string plainText = "super-secret-value";

        var cipherText = await service.EncryptAsync(plainText);

        Assert.NotEqual(plainText, cipherText);
        Assert.DoesNotContain(plainText, cipherText);
    }

    [Fact]
    public async Task EncryptAsync_ProducesADifferentCiphertextEachTime()
    {
        var service = Create();
        const string plainText = "same-input-every-time";

        var first = await service.EncryptAsync(plainText);
        var second = await service.EncryptAsync(plainText);

        Assert.NotEqual(first, second);
        Assert.Equal(plainText, await service.DecryptAsync(first));
        Assert.Equal(plainText, await service.DecryptAsync(second));
    }

    [Fact]
    public async Task DecryptAsync_ThrowsWhenTheCiphertextIsNotBase64()
    {
        var service = Create();

        await Assert.ThrowsAsync<FormatException>(() => service.DecryptAsync("this is not base64 !!"));
    }

    [Fact]
    public async Task DecryptAsync_ThrowsWhenTheAuthenticationTagDoesNotMatch()
    {
        var service = Create();

        var garbage = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => service.DecryptAsync(garbage));
    }

    [Fact]
    public async Task DecryptAsync_ThrowsWhenTheCiphertextWasProducedWithADifferentKey()
    {
        var cipherText = await Create().EncryptAsync("tenant-a-secret");
        var otherService = Create("fedcba9876543210fedcba9876543210");

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => otherService.DecryptAsync(cipherText));
    }

    [Fact]
    public async Task EncryptAndDecrypt_ReturnEmptyForEmptyInput()
    {
        var service = Create();

        Assert.Equal(string.Empty, await service.EncryptAsync(string.Empty));
        Assert.Equal(string.Empty, await service.DecryptAsync(string.Empty));
    }

    [Fact]
    public void Constructor_ThrowsWhenTheKeyIsNot32Bytes()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create("too-short"));

        Assert.Contains("32 bytes", exception.Message);
    }

    [Fact]
    public void Constructor_ThrowsWhenTheKeyIsMissingOutsideDevelopment()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create(key: null));

        Assert.Contains("required outside Development", exception.Message);
    }

    [Fact]
    public async Task Constructor_FallsBackToTheDevelopmentKeyInDevelopmentOnly()
    {
        var service = Create(key: null, environment: "Development");

        var cipherText = await service.EncryptAsync("dev-value");

        Assert.Equal("dev-value", await service.DecryptAsync(cipherText));
    }
}
