using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public sealed record TransportAttempt(
    int StatusCode,
    string Response,
    IReadOnlyDictionary<string, string[]> ResponseHeaders,
    string RequestUrl,
    string HttpMethod,
    IReadOnlyDictionary<string, string[]> RequestHeaders,
    string RequestBody,
    DateTime StartedAt,
    DateTime CompletedAt);

public sealed record TransportResponse(
    int StatusCode,
    string Response,
    IReadOnlyDictionary<string, string[]> Headers,
    string RequestUrl)
{
    public string HttpMethod { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string[]> RequestHeaders { get; init; } = new Dictionary<string, string[]>();
    public string RequestBody { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public IReadOnlyList<TransportAttempt> Attempts { get; init; } = Array.Empty<TransportAttempt>();
}

public interface ITransportEngine
{
    Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default);
}
