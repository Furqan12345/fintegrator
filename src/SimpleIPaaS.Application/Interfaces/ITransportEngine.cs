using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public sealed record TransportResponse(
    int StatusCode,
    string Response,
    IReadOnlyDictionary<string, string[]> Headers,
    string RequestUrl);

public interface ITransportEngine
{
    Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default);
}
