using System.Collections.Generic;
using System.Linq;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public static class ConnectionReducers
{
    [ReducerMethod]
    public static ConnectionState ReduceLoadConnectionsAction(ConnectionState state, LoadConnectionsAction action) =>
        new ConnectionState(isLoading: true, connections: state.Connections);

    [ReducerMethod]
    public static ConnectionState ReduceLoadConnectionsResultAction(ConnectionState state, LoadConnectionsResultAction action) =>
        new ConnectionState(isLoading: false, connections: action.Connections.ToList());

    [ReducerMethod]
    public static ConnectionState ReduceSaveConnectionAction(ConnectionState state, SaveConnectionAction action) =>
        new ConnectionState(isLoading: true, connections: state.Connections);

    [ReducerMethod]
    public static ConnectionState ReduceSaveConnectionResultAction(ConnectionState state, SaveConnectionResultAction action)
    {
        var list = state.Connections.ToList();
        var index = list.FindIndex(c => c.Id == action.Connection.Id);
        if (index >= 0)
            list[index] = action.Connection;
        else
            list.Add(action.Connection);

        return new ConnectionState(isLoading: false, connections: list);
    }

    [ReducerMethod]
    public static ConnectionState ReduceSaveConnectionFailedAction(ConnectionState state, SaveConnectionFailedAction action) =>
        new ConnectionState(isLoading: false, connections: state.Connections);
}
