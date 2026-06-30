using System.Net.Http;
using System.Threading.Tasks;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public interface IAuthenticationHandler
{
    Task AuthenticateAsync(HttpRequestMessage request, string configJson);
}

public interface IAuthenticationHandlerFactory
{
    IAuthenticationHandler GetHandler(AuthType authType);
}
