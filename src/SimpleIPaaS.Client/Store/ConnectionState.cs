using System.Collections.Generic;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

[FeatureState]
public class ConnectionState
{
    public bool IsLoading { get; }
    public IReadOnlyList<ConnectionDto> Connections { get; }

    private ConnectionState() 
    { 
        IsLoading = false;
        Connections = new List<ConnectionDto>();
    } 

    public ConnectionState(bool isLoading, IReadOnlyList<ConnectionDto> connections)
    {
        IsLoading = isLoading;
        Connections = connections;
    }
}
