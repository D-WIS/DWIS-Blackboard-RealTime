using System.Runtime.CompilerServices;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Semantic;

/// <summary>A semantic service that is never there: every call answers <see cref="MethodStatus.BadCommunicationError"/>. For a process started without one, and for tests of the façade's shape.</summary>
public sealed class UnavailableSemanticService : ISemanticService
{
    private static SemanticException Down() => new(MethodStatus.BadCommunicationError, "No semantic service is configured.");

    /// <inheritdoc />
    public Task<PrepareResult> PrepareAsync(PrepareRequest request, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task AbortAsync(ManifestRef manifest, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<bool> RemoveAsync(ManifestRef manifest, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<ManifestList> ListAsync(CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<QueryResult> QueryAsync(string sparql, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<ResourceFile> GetResourcesAsync(CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<QuerySnapshot> RegisterQueryAsync(string sparql, string owner, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<UnregisterResult> UnregisterQueryAsync(string sparql, string owner, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> ReleaseOwnerAsync(string owner, CancellationToken cancellationToken) => throw Down();

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryDiff> WatchAsync(string watcher, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw Down();
#pragma warning disable CS0162 // unreachable: the iterator needs a yield to be one
        yield break;
#pragma warning restore CS0162
    }
}
