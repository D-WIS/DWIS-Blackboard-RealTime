namespace DWIS.RealTime.Core.Adapters;

/// <summary>
/// The nodes of a registered query for one session: the <c>QueryClient{id}_{hash}</c> object and its
/// <c>QueryDiff{id}_{hash}</c> string variable, named as the old server names them (<see cref="Naming.QueryClientNodeId"/>).
/// Implemented by the adapter, called by Core. The client parses the variable's node id from the first output of
/// <c>RegisterQuery</c> and subscribes to its data change; no event is raised.
/// </summary>
public interface IQueryNodes
{
    /// <summary>Creates the object and the variable for a query and a session; returns the variable's address. Creating them twice is harmless.</summary>
    NodeAddress CreateQueryNodes(int queryId, SessionId session);

    /// <summary>Deletes both nodes. Deleting unknown nodes is harmless.</summary>
    void DeleteQueryNodes(int queryId, SessionId session);

    /// <summary>Writes a diff (the <c>QueryResultsDiff</c> JSON) to the variable: the data change the client subscribes to.</summary>
    void WriteDiff(int queryId, SessionId session, string json);
}
