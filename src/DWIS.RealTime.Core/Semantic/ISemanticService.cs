using DWIS.Blackboard.Contracts.Grpc;

namespace DWIS.RealTime.Core.Semantic;

/// <summary>
/// What Core asks the semantic service (specification 3.3), in the messages of the contract. The gRPC
/// implementation is <see cref="GrpcSemanticService"/>; the tests use a fake. A call that cannot reach the
/// service, or that the service refuses, throws a <see cref="SemanticException"/> with the status to answer.
/// </summary>
public interface ISemanticService
{
    /// <summary>Validate, pre-reason and stage a manifest; the variables to create come back (C.5).</summary>
    Task<PrepareResult> PrepareAsync(PrepareRequest request, CancellationToken cancellationToken);

    /// <summary>Make a staged manifest live, with the address of every variable (R-RT-5).</summary>
    Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken cancellationToken);

    /// <summary>Drop a staged manifest.</summary>
    Task AbortAsync(ManifestRef manifest, CancellationToken cancellationToken);

    /// <summary>Remove a live manifest; false when it was not live.</summary>
    Task<bool> RemoveAsync(ManifestRef manifest, CancellationToken cancellationToken);

    /// <summary>The live manifests with their variables and endpoints, for the rebuild (R-RT-6).</summary>
    Task<ManifestList> ListAsync(CancellationToken cancellationToken);

    /// <summary>Answer a SELECT once (the <c>ResolveQuery</c> façade).</summary>
    Task<QueryResult> QueryAsync(string sparql, CancellationToken cancellationToken);

    /// <summary>The individuals and sentences of the live description (the <c>GetResources</c> façade).</summary>
    Task<ResourceFile> GetResourcesAsync(CancellationToken cancellationToken);

    /// <summary>Register a query for an owner (a session id) that belongs to a watcher (this process's name); the full snapshot comes back (D-51, D-61).</summary>
    Task<QuerySnapshot> RegisterQueryAsync(string sparql, string owner, string watcher, CancellationToken cancellationToken);

    /// <summary>Release an owner's hold on a query; whether the owner held it, and the query's id.</summary>
    Task<UnregisterResult> UnregisterQueryAsync(string sparql, string owner, CancellationToken cancellationToken);

    /// <summary>A session closed: everything it registered goes; the ids of the queries nobody holds any more.</summary>
    Task<IReadOnlyList<int>> ReleaseOwnerAsync(string owner, CancellationToken cancellationToken);

    /// <summary>Every diff of every registered query, until the stream ends (D-53); ends by throwing <see cref="SemanticException"/> or by cancellation. Opened fresh, the service first releases every owner of the watcher (D-61).</summary>
    IAsyncEnumerable<QueryDiff> WatchAsync(string watcher, bool fresh, CancellationToken cancellationToken);
}
