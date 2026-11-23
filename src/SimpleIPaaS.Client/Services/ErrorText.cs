using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleIPaaS.Client.Services;

/// <summary>
/// Turns an exception into a sentence a user can act on.
/// The Release WebAssembly build trims the framework's resource strings, so <c>Exception.Message</c>
/// for HTTP and JSON failures is a resource key such as
/// "net_http_message_not_success_statuscode_reason, 500, Internal Server Error". Never show that.
/// </summary>
public static partial class ErrorText
{
    public static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } =>
            $"The server returned HTTP {(int)status} ({ReasonPhrase(status)}).",
        HttpRequestException =>
            "The API could not be reached. Check that it is running, then try again.",
        TaskCanceledException or TimeoutException =>
            "The request timed out. Try again.",
        JsonException json when json.LineNumber.HasValue =>
            $"Invalid JSON at line {json.LineNumber + 1}, position {json.BytePositionInLine + 1}.",
        JsonException =>
            "The text is not valid JSON.",
        _ when LooksLikeResourceKey(exception.Message) =>
            "An unexpected error occurred.",
        _ => exception.Message,
    };

    // HttpStatusCode.InternalServerError -> "Internal Server Error".
    private static string ReasonPhrase(HttpStatusCode status)
    {
        var name = status.ToString();
        return int.TryParse(name, out _) ? "unexpected status" : PascalWords().Replace(name, " $1");
    }

    // Trimmed resource keys are lower_snake_case identifiers ("net_http_...", "Arg_NullReference...").
    private static bool LooksLikeResourceKey(string message) =>
        !string.IsNullOrWhiteSpace(message) && ResourceKey().IsMatch(message);

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex PascalWords();

    [GeneratedRegex(@"^[A-Za-z]+(_[A-Za-z0-9]+)+(,|$)")]
    private static partial Regex ResourceKey();
}
