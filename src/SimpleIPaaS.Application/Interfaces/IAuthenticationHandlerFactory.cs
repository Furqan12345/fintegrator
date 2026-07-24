using System;
using System.Net.Http;
using System.Threading.Tasks;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public class AuthenticationContext
{
    public string ConfigJson { get; set; } = string.Empty;
    public Guid? ConnectionId { get; set; }
    public bool ForceRefresh { get; set; }
}

public interface IAuthenticationHandler
{
    Task AuthenticateAsync(HttpRequestMessage request, string configJson);
    Task AuthenticateAsync(HttpRequestMessage request, AuthenticationContext context);
}

public interface IAuthenticationHandlerFactory
{
    IAuthenticationHandler GetHandler(AuthType authType);
}
