using System.Net;
using System.Net.Http.Headers;

namespace SimpleIPaaS.Client.Services;

public class AuthTokenHandler : DelegatingHandler
{
    private readonly AuthSession _session;

    public AuthTokenHandler(AuthSession session)
    {
        _session = session;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var token = await _session.GetAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var retryRequest = await CloneAsync(request, cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || string.IsNullOrWhiteSpace(token))
        {
            retryRequest.Dispose();
            return response;
        }

        response.Dispose();
        if (!await _session.RefreshAsync())
        {
            retryRequest.Dispose();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                RequestMessage = request,
                ReasonPhrase = "Session expired"
            };
        }

        var refreshedToken = _session.AccessToken;
        if (!string.IsNullOrWhiteSpace(refreshedToken))
        {
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
        }

        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
