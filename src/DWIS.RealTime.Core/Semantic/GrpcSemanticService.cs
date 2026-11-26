using System.Runtime.CompilerServices;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.RealTime.Core.Adapters;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace DWIS.RealTime.Core.Semantic;

/// <summary>
/// The semantic service over gRPC (specification 3.3, D-10, D-50): one channel, a deadline on every call but the
/// watch stream, and the gRPC status codes mapped to the façade's statuses: the service unreachable, too slow or
/// not ready answers <see cref="MethodStatus.BadCommunicationError"/>; a refused input <see cref="MethodStatus.BadInvalidArgument"/>.
/// </summary>
public sealed class GrpcSemanticService : ISemanticService, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly Blackboard.Contracts.Grpc.Semantic.SemanticClient _client;
    private readonly TimeSpan _deadline;

    /// <summary>Opens a channel to the service (<c>http://</c>: HTTP/2 without TLS in the lab profile).</summary>
    public GrpcSemanticService(string address, TimeSpan deadline)
    {
        _channel = GrpcChannel.ForAddress(address);
        _client = new Blackboard.Contracts.Grpc.Semantic.SemanticClient(_channel);
        _deadline = deadline;
    }

    private CallOptions Options(CancellationToken cancellationToken) => new(deadline: DateTime.UtcNow + _deadline, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task<PrepareResult> PrepareAsync(PrepareRequest request, CancellationToken cancellationToken) =>
        Call(() => _client.PrepareManifestAsync(request, Options(cancellationToken)).ResponseAsync, "PrepareManifest");

    /// <inheritdoc />
    public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken cancellationToken) =>
        Call(() => _client.CommitManifestAsync(request, Options(cancellationToken)).ResponseAsync, "CommitManifest");

    /// <inheritdoc />
    public Task AbortAsync(ManifestRef manifest, CancellationToken cancellationToken) =>
        Call(() => _client.AbortManifestAsync(manifest, Options(cancellationToken)).ResponseAsync, "AbortManifest");

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(ManifestRef manifest, CancellationToken cancellationToken) =>
        (await Call(() => _client.RemoveManifestAsync(manifest, Options(cancellationToken)).ResponseAsync, "RemoveManifest").ConfigureAwait(false)).Removed;

    /// <inheritdoc />
    public Task<ManifestList> ListAsync(CancellationToken cancellationToken) =>
        Call(() => _client.ListManifestsAsync(new Empty(), Options(cancellationToken)).ResponseAsync, "ListManifests");

    /// <inheritdoc />
    public Task<QueryResult> QueryAsync(string sparql, CancellationToken cancellationToken) =>
        Call(() => _client.QueryAsync(new SparqlRequest { Query = sparql }, Options(cancellationToken)).ResponseAsync, "Query");

    /// <inheritdoc />
    public Task<ResourceFile> GetResourcesAsync(CancellationToken cancellationToken) =>
        Call(() => _client.GetResourcesAsync(new Empty(), Options(cancellationToken)).ResponseAsync, "GetResources");

    /// <inheritdoc />
    public Task<QuerySnapshot> RegisterQueryAsync(string sparql, string owner, string watcher, CancellationToken cancellationToken) =>
        Call(() => _client.RegisterQueryAsync(new RegisterRequest { Query = sparql, Owner = owner, Watcher = watcher }, Options(cancellationToken)).ResponseAsync, "RegisterQuery");

    /// <inheritdoc />
    public Task<UnregisterResult> UnregisterQueryAsync(string sparql, string owner, CancellationToken cancellationToken) =>
        Call(() => _client.UnregisterQueryAsync(new UnregisterRequest { Query = sparql, Owner = owner }, Options(cancellationToken)).ResponseAsync, "UnregisterQuery");

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ReleaseOwnerAsync(string owner, CancellationToken cancellationToken) =>
        (await Call(() => _client.ReleaseOwnerAsync(new OwnerRef { Owner = owner }, Options(cancellationToken)).ResponseAsync, "ReleaseOwner").ConfigureAwait(false)).QueryIds.ToList();

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryDiff> WatchAsync(string watcher, bool fresh, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // No deadline: the stream lives as long as the service and the connection do.
        using var stream = _client.WatchQueries(new WatchRequest { Watcher = watcher, Fresh = fresh }, cancellationToken: cancellationToken);
        var reader = stream.ResponseStream;
        while (true)
        {
            bool more;
            try
            {
                more = await reader.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException e)
            {
                throw new SemanticException(StatusOf(e.StatusCode), $"WatchQueries: {e.Status.Detail} ({e.StatusCode})", e);
            }
            if (!more)
            {
                yield break;
            }
            yield return reader.Current;
        }
    }

    private static async Task<T> Call<T>(Func<Task<T>> call, string name)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException e)
        {
            throw new SemanticException(StatusOf(e.StatusCode), $"{name}: {e.Status.Detail} ({e.StatusCode})", e);
        }
    }

    /// <summary>The façade status for a gRPC status code.</summary>
    public static MethodStatus StatusOf(StatusCode code) => code switch
    {
        StatusCode.InvalidArgument => MethodStatus.BadInvalidArgument,
        StatusCode.NotFound => MethodStatus.BadInvalidArgument,
        StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled or StatusCode.FailedPrecondition or StatusCode.ResourceExhausted => MethodStatus.BadCommunicationError,
        _ => MethodStatus.BadInternalError,
    };

    /// <inheritdoc />
    public void Dispose() => _channel.Dispose();
}
