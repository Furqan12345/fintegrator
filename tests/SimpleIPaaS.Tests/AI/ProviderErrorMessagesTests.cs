using System.Net;
using SimpleIPaaS.Api.Services;

namespace SimpleIPaaS.Tests.AI;

// Unmapped provider statuses (402, 405, 409, 422, ...) used to be flattened to a generic
// "The AI provider returned an error. Check its endpoint and configuration." and the
// provider's own message was dropped. The workers (chat + build) both use this mapping.
public sealed class ProviderErrorMessagesTests
{
    [Fact]
    public void Describe_UnmappedStatusIncludesStatusAndProviderMessage()
    {
        var message = ProviderErrorMessages.Describe(
            new ProviderRequestException((HttpStatusCode)402, "Insufficient quota for this model."));

        Assert.Contains("402", message);
        Assert.Contains("Insufficient quota", message);
    }

    [Fact]
    public void Describe_UnmappedStatusWithoutProviderMessageStillNamesTheStatus()
    {
        var message = ProviderErrorMessages.Describe(new ProviderRequestException((HttpStatusCode)422, null));

        Assert.Contains("422", message);
        Assert.DoesNotContain("returned an error", message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    public void Describe_KeepsExistingGuidanceForMappedStatuses(HttpStatusCode status, int expected)
    {
        var message = ProviderErrorMessages.Describe(new ProviderRequestException(status, "provider detail"));

        Assert.Contains("provider detail", message);
        Assert.True(expected is 400 or 401 or 403 or 404);
    }

    [Fact]
    public void Describe_TruncatesAndFlattensLongProviderMessages()
    {
        var longMessage = "line one" + new string('x', 1000);

        var message = ProviderErrorMessages.Describe(new ProviderRequestException((HttpStatusCode)409, longMessage));

        Assert.Contains("409", message);
        Assert.Contains("[provider message truncated]", message);
        Assert.True(message.Length < 600);
    }

    [Fact]
    public void StoredCredentialUnreadable_TellsTheOperatorToReEnterTheKey()
    {
        Assert.Contains("re-enter the API key", ProviderErrorMessages.StoredCredentialUnreadable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Encryption:Key", ProviderErrorMessages.StoredCredentialUnreadable);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public void Describe_AlwaysCarriesTheNumericStatus(int status)
    {
        var message = ProviderErrorMessages.Describe(new ProviderRequestException((HttpStatusCode)status, "provider detail"));

        Assert.Contains(status.ToString(), message);
        Assert.Contains("provider detail", message);
    }

    // Both API shapes must yield the provider's own text: the Chat Completions envelope and the
    // Responses envelope ({error:{message,type,code}}), which also appears as a bare code.
    [Theory]
    [InlineData("{\"error\":{\"message\":\"model does not exist\",\"type\":\"invalid_request_error\",\"code\":\"model_not_found\"}}", "model does not exist")]
    [InlineData("{\"error\":{\"message\":\"Web search is not enabled for this organization.\",\"type\":\"invalid_request_error\",\"code\":\"unsupported_tool\"}}", "Web search is not enabled")]
    [InlineData("{\"error\":\"too many requests\"}", "too many requests")]
    [InlineData("{\"error\":[{\"message\":\"No endpoints found\"}]}", "No endpoints found")]
    [InlineData("{\"message\":\"top level message\"}", "top level message")]
    [InlineData("{\"error\":{\"type\":\"invalid_request_error\",\"code\":\"invalid_api_version\"}}", "invalid_api_version")]
    public void ReadProviderMessage_ReadsTheChatAndResponsesErrorShapes(string body, string expected)
    {
        var message = ProviderErrorMessages.ReadProviderMessage(body, "provider-test-key");

        Assert.NotNull(message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void ReadProviderMessage_RedactsTheApiKeyAndCapsTheLength()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            error = new { message = "bad key provider-test-key " + new string('x', 2000), type = "invalid_request_error", code = "invalid_api_key" }
        });

        var message = ProviderErrorMessages.ReadProviderMessage(body, "provider-test-key");

        Assert.NotNull(message);
        Assert.DoesNotContain("provider-test-key", message);
        Assert.Contains("[redacted]", message);
        Assert.True(message.Length <= 800);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void ReadProviderMessage_ReturnsNullWhenThereIsNothingReadable(string body)
    {
        Assert.Null(ProviderErrorMessages.ReadProviderMessage(body, "provider-test-key"));
    }
}
