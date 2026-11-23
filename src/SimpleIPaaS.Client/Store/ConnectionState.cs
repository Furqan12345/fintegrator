using System.Collections.Generic;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

[FeatureState]
public class ConnectionState
{
    public bool IsLoading { get; }
    public IReadOnlyList<ConnectionDto> Connections { get; }
    /// <summary>The last load failed; the page shows a retry state instead of "no connections yet".</summary>
    public bool LoadFailed { get; }

    private ConnectionState() 
    { 
        IsLoading = false;
        Connections = new List<ConnectionDto>();
    } 

    public ConnectionState(bool isLoading, IReadOnlyList<ConnectionDto> connections, bool loadFailed = false)
    {
        IsLoading = isLoading;
        Connections = connections;
        LoadFailed = loadFailed;
    }
}
