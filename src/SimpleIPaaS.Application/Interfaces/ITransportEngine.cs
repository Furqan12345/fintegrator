using System;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface ITransportEngine
{
    Task<(int StatusCode, string Response)> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null);
}
