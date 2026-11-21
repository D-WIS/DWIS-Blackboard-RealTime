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
}
