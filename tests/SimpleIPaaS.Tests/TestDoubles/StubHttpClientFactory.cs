using System.Net;

namespace SimpleIPaaS.Tests.TestDoubles;

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public static StubHttpMessageHandler Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        return _responder(request);
    }
}

public sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public static StubHttpClientFactory Unreachable()
    {
        return new StubHttpClientFactory(new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("No network call was expected in this test.")));
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

    public void Dispose() => _handler.Dispose();
}
