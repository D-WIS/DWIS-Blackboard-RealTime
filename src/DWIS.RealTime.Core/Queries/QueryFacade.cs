using DWIS.Blackboard.Contracts.Grpc;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Semantic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DWIS.RealTime.Core.Queries;

/// <summary>
/// The query side of the façade (specification 3.1 and 3.5; R-QR-1 to R-QR-3; decisions D-31, D-51, D-53):
/// <c>ResolveQuery</c> and <c>GetResources</c> straight to the service; <c>RegisterQuery</c> with the session as
/// the owner, the <c>QueryClient</c> and <c>QueryDiff</c> nodes per session; the one watch stream fanned out to the
/// sessions' diff variables; <c>UnregisterQuery</c> and the session close releasing what the session held.
/// </summary>
public sealed partial class QueryFacade
{
    /// <summary>The name this process gives the service on its watch stream.</summary>
    public const string Watcher = "real-time";

    private readonly IQueryNodes _nodes;
    private readonly IMethodHost _methods;
    private readonly ISemanticService _semantic;
    private readonly ILogger _logger;
    private readonly Dictionary<(int LocalId, SessionId Session), NodeAddress> _addresses = [];
    private readonly Lock _gate = new();

    /// <summary>Creates the façade over the adapter and the service.</summary>
    public QueryFacade(IQueryNodes nodes, IMethodHost methods, ISemanticService semantic, ILogger<QueryFacade>? logger = null)
    {
        _nodes = nodes;
        _methods = methods;
        _semantic = semantic;
        _logger = logger ?? NullLogger<QueryFacade>.Instance;
    }

    /// <summary>The registered queries and the sessions that hold them.</summary>
    public QueryRegistry Registry { get; } = new();

    /// <summary>How many times the watch stream was opened (tests).</summary>
    public int StreamsOpened { get; private set; }

    /// <summary><c>ResolveQuery</c>: the <c>QueryResult</c> JSON, or the status of a failure.</summary>
    public async Task<(MethodStatus Status, string? Json)> ResolveAsync(string sparql, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _semantic.QueryAsync(sparql, cancellationToken).ConfigureAwait(false);
            return (MethodStatus.Good, QueryJson.Result(result.Variables, result.Rows));
        }
        catch (SemanticException e)
        {
            Log.CallFailed(_logger, e, "ResolveQuery", e.Status);
            return (e.Status, null);
        }
    }

    /// <summary><c>GetResources</c>: the <c>ResourceFile</c> JSON, or the status of a failure.</summary>
    public async Task<(MethodStatus Status, string? Json)> GetResourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var file = await _semantic.GetResourcesAsync(cancellationToken).ConfigureAwait(false);
            return (MethodStatus.Good, QueryJson.Resources(file));
        }
        catch (SemanticException e)
        {
            Log.CallFailed(_logger, e, "GetResources", e.Status);
            return (e.Status, null);
        }
    }

    /// <summary>
    /// <c>RegisterQuery</c>: registers the query for the session, creates its nodes, and answers the initial diff
    /// (the snapshot as added rows, with the diff variable's node id as <c>QueryResultID</c>) and the client node's id.
    /// </summary>
    public async Task<(MethodStatus Status, string? DiffJson, string? NodeId)> RegisterAsync(string sparql, SessionId session, CancellationToken cancellationToken)
    {
        QuerySnapshot snapshot;
        try
        {
            snapshot = await _semantic.RegisterQueryAsync(sparql, session.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (SemanticException e)
        {
            Log.CallFailed(_logger, e, "RegisterQuery", e.Status);
            return (e.Status, null, null);
        }

        var (entry, isNew) = Registry.Add(sparql, snapshot.QueryId, snapshot.Variables, snapshot.Rows, session);
        var address = _nodes.CreateQueryNodes(entry.LocalId, session);
        lock (_gate)
        {
            _addresses[(entry.LocalId, session)] = address;
        }
        UpdateDiagnostics();
        Log.Registered(_logger, entry.LocalId, snapshot.QueryId, session.Value, snapshot.Rows.Count, isNew);
        var diff = QueryJson.Diff(Naming.QueryClientNodeId(entry.LocalId, session), NodeIdText(address), snapshot.Rows, []);
        return (MethodStatus.Good, diff, NodeIdText(address with { NodeId = Naming.QueryClientNodeId(entry.LocalId, session) }));
    }

    /// <summary><c>UnregisterQuery</c>: releases the session's hold, deletes its nodes; answers whether the session held the query.</summary>
    public async Task<(MethodStatus Status, bool Released)> UnregisterAsync(string sparql, SessionId session, CancellationToken cancellationToken)
    {
        UnregisterResult result;
        try
        {
            result = await _semantic.UnregisterQueryAsync(sparql, session.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (SemanticException e)
        {
            Log.CallFailed(_logger, e, "UnregisterQuery", e.Status);
            return (e.Status, false);
        }

        var (entry, _) = Registry.RemoveSession(result.QueryId, session);
        if (entry is not null)
        {
            DeleteNodes(entry.LocalId, session);
        }
        UpdateDiagnostics();
        return (MethodStatus.Good, result.Released);
    }

    /// <summary>A session closed: the service releases what it held, its query nodes go.</summary>
    public async Task SessionClosedAsync(SessionId session, CancellationToken cancellationToken)
    {
        var removed = Registry.RemoveSessionEverywhere(session);
        foreach (var (entry, _) in removed)
        {
            DeleteNodes(entry.LocalId, session);
        }
        UpdateDiagnostics();
        if (removed.Count == 0)
        {
            return;
        }
        try
        {
            var released = await _semantic.ReleaseOwnerAsync(session.Value, cancellationToken).ConfigureAwait(false);
            Log.SessionReleased(_logger, session.Value, removed.Count, released.Count);
        }
        catch (SemanticException e)
        {
            Log.CallFailed(_logger, e, "ReleaseOwner", e.Status);
        }
    }

    /// <summary>
    /// Keeps one watch stream open: every diff goes to the diff variable of every session that holds the query.
    /// When the stream ends (the service restarted, the connection dropped, this subscriber fell behind), waits
    /// <paramref name="retry"/>, registers every held query again for each of its sessions and writes the
    /// difference between the fresh snapshot and the rows last known as a diff (T-QR-4); the nodes stay.
    /// </summary>
    public async Task WatchLoopAsync(TimeSpan retry, CancellationToken cancellationToken)
    {
        var firstRound = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!firstRound)
            {
                try
                {
                    await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                await ReregisterAllAsync(cancellationToken).ConfigureAwait(false);
            }
            firstRound = false;
            try
            {
                StreamsOpened++;
                Log.StreamOpened(_logger, StreamsOpened);
                await foreach (var diff in _semantic.WatchAsync(Watcher, cancellationToken).ConfigureAwait(false))
                {
                    Deliver(diff);
                }
                Log.StreamEnded(_logger, "the service closed it");
            }
            catch (SemanticException e)
            {
                Log.StreamEnded(_logger, e.Message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Deliver(QueryDiff diff)
    {
        var entry = Registry.Apply(diff.QueryId, diff.Added, diff.Removed);
        if (entry is null)
        {
            return;
        }
        foreach (var session in entry.Sessions)
        {
            WriteDiff(entry.LocalId, session, diff.Added, diff.Removed);
        }
        Log.Delivered(_logger, entry.LocalId, diff.QueryId, diff.Added.Count, diff.Removed.Count, entry.Sessions.Count);
    }

    private async Task ReregisterAllAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in Registry.All)
        {
            QuerySnapshot? snapshot = null;
            foreach (var session in entry.Sessions)
            {
                try
                {
                    snapshot = await _semantic.RegisterQueryAsync(entry.Text, session.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (SemanticException e)
                {
                    Log.CallFailed(_logger, e, "RegisterQuery (again)", e.Status);
                    return;
                }
            }
            if (snapshot is null)
            {
                continue;
            }
            var rebound = Registry.Rebind(entry.LocalId, snapshot.QueryId, snapshot.Variables, snapshot.Rows);
            if (rebound is not { } pair)
            {
                continue;
            }
            var added = QueryRegistry.Except(pair.After.Rows, pair.Before.Rows);
            var removed = QueryRegistry.Except(pair.Before.Rows, pair.After.Rows);
            if (added.Count > 0 || removed.Count > 0)
            {
                foreach (var session in pair.After.Sessions)
                {
                    WriteDiff(entry.LocalId, session, added, removed);
                }
            }
            Log.Reregistered(_logger, entry.LocalId, snapshot.QueryId, added.Count, removed.Count);
        }
    }

    private void WriteDiff(int localId, SessionId session, IEnumerable<Row> added, IEnumerable<Row> removed)
    {
        var json = QueryJson.Diff(Naming.QueryClientNodeId(localId, session), Naming.QueryDiffNodeId(localId, session), added, removed);
        _nodes.WriteDiff(localId, session, json);
    }

    private void DeleteNodes(int localId, SessionId session)
    {
        _nodes.DeleteQueryNodes(localId, session);
        lock (_gate)
        {
            _addresses.Remove((localId, session));
        }
    }

    private void UpdateDiagnostics()
    {
        _methods.SetDiagnostic(DDHubMethods.RegisteredQueryCount, Registry.QueryCount);
        _methods.SetDiagnostic(DDHubMethods.RegisteredQuerySubscriptionCount, Registry.SubscriptionCount);
    }

    /// <summary>A node id as OPC UA writes it for a string id: <c>ns={index};s={id}</c>, what the old server answered and the client parses.</summary>
    public static string NodeIdText(NodeAddress address) => $"ns={address.NamespaceIndex};s={address.NodeId}";

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "{Method} failed: {Status}")]
        public static partial void CallFailed(ILogger logger, Exception exception, string method, MethodStatus status);

        [LoggerMessage(Level = LogLevel.Information, Message = "Query {LocalId} (service id {ServiceId}) registered for session {Session}: {RowCount} rows; new here: {IsNew}")]
        public static partial void Registered(ILogger logger, int localId, int serviceId, string session, int rowCount, bool isNew);

        [LoggerMessage(Level = LogLevel.Information, Message = "Session {Session} closed: {HeldCount} queries released here, {GoneCount} gone in the service")]
        public static partial void SessionReleased(ILogger logger, string session, int heldCount, int goneCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Watch stream {Round} opened")]
        public static partial void StreamOpened(ILogger logger, int round);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Watch stream ended: {Reason}; registering again after the retry delay")]
        public static partial void StreamEnded(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Diff of query {LocalId} (service id {ServiceId}): +{Added} -{Removed} to {SessionCount} sessions")]
        public static partial void Delivered(ILogger logger, int localId, int serviceId, int added, int removed, int sessionCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Query {LocalId} registered again as service id {ServiceId}: +{Added} -{Removed} since the stream ended")]
        public static partial void Reregistered(ILogger logger, int localId, int serviceId, int added, int removed);
    }
}
