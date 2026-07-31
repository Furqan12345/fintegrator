using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Services;

namespace SimpleIPaaS.Tests.Scripting;

public class AdvancedCodeExecutionServiceTests
{
    private static AdvancedCodeExecutionService Create(int timeoutSeconds = 60)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scripting:TimeoutSeconds"] = timeoutSeconds.ToString()
            })
            .Build();

        return new AdvancedCodeExecutionService(configuration);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ReturnsScriptResult()
    {
        var service = Create();

        var result = await service.ExecuteMappingAsync(
            "var doc = JObject.Parse(FlowStateJson); return doc[\"name\"].ToString().ToUpper();",
            "{\"name\":\"acme\"}",
            "{}");

        Assert.Equal("ACME", result);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ExposesPersistedState()
    {
        var service = Create();

        var result = await service.ExecuteMappingAsync(
            "return JObject.Parse(PersistedStateJson)[\"cursor\"].ToString();",
            "{}",
            "{\"cursor\":\"42\"}");

        Assert.Equal("42", result);
    }

    [Fact]
    public async Task ExecutePostFlightAsync_ExposesHttpResponse()
    {
        var service = Create();

        var result = await service.ExecutePostFlightAsync(
            "return JObject.Parse(HttpResponseJson)[\"id\"].ToString();",
            "{}",
            "{}",
            "{\"id\":\"post-flight\"}");

        Assert.Equal("post-flight", result);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ThrowsScriptExecutionException_WhenScriptThrows()
    {
        var service = Create();

        var exception = await Assert.ThrowsAsync<ScriptExecutionException>(() =>
            service.ExecuteMappingAsync("throw new InvalidOperationException(\"mapping blew up\");", "{}", "{}"));

        Assert.False(exception.TimedOut);
        Assert.Contains("mapping blew up", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ThrowsScriptExecutionException_WhenScriptDoesNotCompile()
    {
        var service = Create();

        var exception = await Assert.ThrowsAsync<ScriptExecutionException>(() =>
            service.ExecuteMappingAsync("this is not valid c#", "{}", "{}"));

        Assert.False(exception.TimedOut);
    }

    [Fact]
    public async Task ExecuteMappingAsync_EscapesExceptionMessage_SoItIsJsonSafe()
    {
        var service = Create();

        var exception = await Assert.ThrowsAsync<ScriptExecutionException>(() =>
            service.ExecuteMappingAsync(
                "throw new InvalidOperationException(\"quote \\\" and newline \\n here\");",
                "{}",
                "{}"));

        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain('"', exception.Message);
        Assert.Equal(
            "quote \" and newline \n here",
            JsonSerializer.Deserialize<string>($"\"{exception.Message}\""));
    }

    [Fact]
    public async Task ExecuteBranchAsync_ReturnsPredicateResult()
    {
        var service = Create();

        var isTrue = await service.ExecuteBranchAsync(
            "return JObject.Parse(FlowStateJson)[\"count\"].Value<int>() > 5;", "{\"count\":9}", "{}");
        var isFalse = await service.ExecuteBranchAsync(
            "return JObject.Parse(FlowStateJson)[\"count\"].Value<int>() > 5;", "{\"count\":1}", "{}");

        Assert.True(isTrue);
        Assert.False(isFalse);
    }

    [Fact]
    public async Task ExecuteBranchAsync_ThrowsRatherThanSilentlyReturningFalse_WhenPredicateFails()
    {
        var service = Create();

        var exception = await Assert.ThrowsAsync<ScriptExecutionException>(() =>
            service.ExecuteBranchAsync("return JObject.Parse(FlowStateJson)[\"missing\"].Value<bool>();", "{}", "{}"));

        Assert.False(exception.TimedOut);
    }

    [Fact]
    public async Task ExecuteUrlAsync_ResolvesDynamicUrl()
    {
        var service = Create();

        var url = await service.ExecuteUrlAsync(
            "return \"https://example.test/orders/\" + JObject.Parse(FlowStateJson)[\"id\"];", "{\"id\":7}", "{}");

        Assert.Equal("https://example.test/orders/7", url);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ReusesCompiledScript_AndReturnsCorrectResultEachTime()
    {
        var service = Create();
        const string code = "return \"cache-probe:\" + JObject.Parse(FlowStateJson)[\"n\"];";

        var first = await service.ExecuteMappingAsync(code, "{\"n\":1}", "{}");
        var second = await service.ExecuteMappingAsync(code, "{\"n\":2}", "{}");
        var third = await new AdvancedCodeExecutionServiceCacheProbe().RunAsync(code);

        Assert.Equal("cache-probe:1", first);
        Assert.Equal("cache-probe:2", second);
        Assert.Equal("cache-probe:3", third);
    }

    [Fact]
    public async Task ExecuteMappingAsync_ReturnsEmptyString_WhenScriptReturnsNull()
    {
        var service = Create();

        var result = await service.ExecuteMappingAsync("return (string)null;", "{}", "{}");

        Assert.Equal(string.Empty, result);
    }

    private sealed class AdvancedCodeExecutionServiceCacheProbe
    {
        public Task<string> RunAsync(string code)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Scripting:TimeoutSeconds"] = "60" })
                .Build();

            return new AdvancedCodeExecutionService(configuration).ExecuteMappingAsync(code, "{\"n\":3}", "{}");
        }
    }
}
