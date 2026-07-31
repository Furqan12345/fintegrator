using Microsoft.Extensions.Configuration;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Services;

namespace SimpleIPaaS.Tests.Scripting;

public class CodeExecutionServiceTests
{
    private static CodeExecutionService Create()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scripting:TimeoutSeconds"] = "60"
            })
            .Build();

        return new CodeExecutionService(configuration);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ReturnsScriptResult()
    {
        var service = Create();

        var result = await service.ExecuteMappingAsync(
            "return JObject.Parse(InputJson)[\"greeting\"].ToString();", "{\"greeting\":\"hello\"}");

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ThrowsScriptExecutionException_WhenScriptThrows()
    {
        var service = Create();

        var exception = await Assert.ThrowsAsync<ScriptExecutionException>(() =>
            service.ExecuteMappingAsync("throw new InvalidOperationException(\"legacy mapping failure\");", "{}"));

        Assert.False(exception.TimedOut);
        Assert.Contains("legacy mapping failure", exception.Message);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ThrowsScriptExecutionException_WhenScriptDoesNotCompile()
    {
        var service = Create();

        await Assert.ThrowsAsync<ScriptExecutionException>(() =>
            service.ExecuteMappingAsync("return 12345;", "{}"));
    }

    [Fact]
    public async Task ExecuteMappingAsync_ReusesCompiledScript_AndReturnsCorrectResultEachTime()
    {
        var service = Create();
        const string code = "return \"legacy-cache:\" + JObject.Parse(InputJson)[\"n\"];";

        var first = await service.ExecuteMappingAsync(code, "{\"n\":1}");
        var second = await service.ExecuteMappingAsync(code, "{\"n\":2}");

        Assert.Equal("legacy-cache:1", first);
        Assert.Equal("legacy-cache:2", second);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ReturnsEmptyString_WhenScriptReturnsNull()
    {
        var service = Create();

        var result = await service.ExecuteMappingAsync("return (string)null;", "{}");

        Assert.Equal(string.Empty, result);
    }
}
