using DWIS.Blackboard.Contracts.Grpc;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Semantic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DWIS.RealTime.Core.Injection;

/// <summary>
/// The injection side of the façade (specification 3.1 and 4, Appendix C.5): the two-phase injection over the
/// contract, the provider folders and variables with their validity booleans, the rebuild from the service, and
/// the rules of decision D-52. Nothing here touches a variable's value path (R-RT-1).
/// </summary>
public sealed partial class Injector : IDisposable
{
    private readonly IAddressSpace _space;
    private readonly ISemanticService _semantic;
    private readonly LiveManifests _live;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>Creates the injector over the adapter and the service.</summary>
    public Injector(IAddressSpace space, ISemanticService semantic, LiveManifests live, ILogger<Injector>? logger = null)
    {
        _space = space;
        _semantic = semantic;
        _live = live;
        _logger = logger ?? NullLogger<Injector>.Instance;
    }

    /// <summary>The outcome of an injection: the status, and the result JSON when the manifest went live.</summary>
    public sealed record Outcome(MethodStatus Status, string? ResultJson = null, bool Removed = false);

    /// <summary>
    /// Inject (or update: the same path, the service replaces a live id) a manifest from its JSON: prepare, create
    /// the nodes, commit; the variables of a previous version that the new one lacks go after the commit.
    /// </summary>
    public async Task<Outcome> InjectAsync(string manifestJson, CancellationToken cancellationToken = default)
    {
        var (manifest, key) = ManifestKey.Parse(manifestJson);
        if (manifest is null || key is null)
        {
            Log.NotAManifest(_logger);
            return new Outcome(MethodStatus.BadInvalidArgument, InjectionResultJson.Failure());
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _live.Get(key);
            PrepareResult prepared;
            try
            {
                prepared = await _semantic.PrepareAsync(new PrepareRequest { ManifestJson = manifestJson, Origin = "local" }, cancellationToken).ConfigureAwait(false);
            }
            catch (SemanticException e)
            {
                Log.PrepareFailed(_logger, e, key.Value, e.Status);
                return new Outcome(e.Status, InjectionResultJson.Failure());
            }

            key = ManifestKey.Of(prepared.Manifest);
            var created = new List<string>();
            List<(string VariableId, NodeAddress Address)> addresses;
            try
            {
                addresses = CreateNodes(key.Provider, prepared.Variables, previous?.VariableIds, created);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.NodesFailed(_logger, e, key.Value);
                RemoveNodes(key.Provider, created, previous is null && !_live.ProviderHasOthers(key.Provider, key));
                await AbortQuietlyAsync(key, cancellationToken).ConfigureAwait(false);
                return new Outcome(MethodStatus.BadInternalError, InjectionResultJson.Failure());
            }

            if (prepared.AlreadyLive)
            {
                // Identical content is live: nothing was staged, no commit; the nodes exist (or were just completed).
                _live.Set(new LiveManifest(key, prepared.ContentHash, addresses.Select(a => a.VariableId).ToList()));
                Log.AlreadyLive(_logger, key.Value, addresses.Count);
                return new Outcome(MethodStatus.Good, InjectionResultJson.Success(key.Provider, addresses));
            }

            var commit = new CommitRequest { Manifest = key.ToRef() };
            commit.Endpoints.AddRange(addresses.Select(a => new VariableEndpoint { VariableId = a.VariableId, EndpointUrl = a.Address.EndpointUrl, NamespaceUri = a.Address.NamespaceUri, NodeId = a.Address.NodeId }));
            CommitResult committed;
            try
            {
                committed = await _semantic.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            catch (SemanticException e)
            {
                // D-52 rule (1): an aborted update keeps the nodes of the version that is live; a fresh inject leaves nothing.
                Log.CommitFailed(_logger, e, key.Value, e.Status, previous is not null);
                RemoveNodes(key.Provider, created, previous is null && !_live.ProviderHasOthers(key.Provider, key));
                await AbortQuietlyAsync(key, cancellationToken).ConfigureAwait(false);
                return new Outcome(e.Status, InjectionResultJson.Failure());
            }

            var current = addresses.Select(a => a.VariableId).ToList();
            if (previous is not null)
            {
                RemoveNodes(key.Provider, previous.VariableIds.Except(current).ToList(), false);
            }
            _live.Set(new LiveManifest(key, prepared.ContentHash, current));
            Log.Injected(_logger, key.Value, committed.Accepted, current.Count, previous is not null);
            return new Outcome(MethodStatus.Good, InjectionResultJson.Success(key.Provider, addresses));
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Remove a manifest from its JSON (the <c>Delete</c> method): the service first, then the nodes.</summary>
    public async Task<Outcome> DeleteAsync(string manifestJson, CancellationToken cancellationToken = default)
    {
        var (manifest, key) = ManifestKey.Parse(manifestJson);
        if (manifest is null || key is null)
        {
            Log.NotAManifest(_logger);
            return new Outcome(MethodStatus.BadInvalidArgument);
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool removed;
            try
            {
                removed = await _semantic.RemoveAsync(key.ToRef(), cancellationToken).ConfigureAwait(false);
            }
            catch (SemanticException e)
            {
                Log.RemoveFailed(_logger, e, key.Value, e.Status);
                return new Outcome(e.Status);
            }

            var live = _live.Remove(key);
            if (live is not null)
            {
                RemoveNodes(key.Provider, live.VariableIds, !_live.ProviderHasOthers(key.Provider, key));
            }
            Log.Removed(_logger, key.Value, removed, live?.VariableIds.Count ?? 0);
            return new Outcome(MethodStatus.Good, Removed: removed || live is not null);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Rebuilds the folders and variables from the service's list (R-RT-6): creates what is missing, forgets nothing
    /// that is live. Throws <see cref="SemanticException"/> when the service does not answer.
    /// </summary>
    public async Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        var list = await _semantic.ListAsync(cancellationToken).ConfigureAwait(false);
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var count = 0;
            foreach (var summary in list.Manifests)
            {
                var key = ManifestKey.Of(summary.Manifest);
                var created = new List<string>();
                var addresses = CreateNodes(key.Provider, summary.Variables, null, created);
                _live.Set(new LiveManifest(key, summary.ContentHash, addresses.Select(a => a.VariableId).ToList()));
                count++;
                if (created.Count > 0)
                {
                    Log.Rebuilt(_logger, key.Value, created.Count);
                }
            }
            return count;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private List<(string VariableId, NodeAddress Address)> CreateNodes(string provider, IEnumerable<ProvidedVariable> variables, IReadOnlyList<string>? existing, List<string> created)
    {
        _space.CreateProviderFolder(provider);
        var addresses = new List<(string, NodeAddress)>();
        foreach (var variable in variables)
        {
            var spec = new VariableSpec(variable.VariableId, variable.DataType, variable.Rank, [.. variable.Dimensions], variable.InitialValue,
                string.IsNullOrEmpty(variable.ValidityVariableId) ? variable.VariableId + Naming.ValiditySuffix : variable.ValidityVariableId);
            var address = _space.CreateVariable(provider, spec);
            if (existing is null || !existing.Contains(variable.VariableId))
            {
                created.Add(variable.VariableId);
            }
            addresses.Add((variable.VariableId, address));
        }
        return addresses;
    }

    private void RemoveNodes(string provider, IReadOnlyList<string> variableIds, bool folderToo)
    {
        foreach (var id in variableIds)
        {
            _space.DeleteVariable(provider, id);
        }
        if (folderToo)
        {
            _space.DeleteProviderFolder(provider);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _oneAtATime.Dispose();

    private async Task AbortQuietlyAsync(ManifestKey key, CancellationToken cancellationToken)
    {
        try
        {
            await _semantic.AbortAsync(key.ToRef(), cancellationToken).ConfigureAwait(false);
        }
        catch (SemanticException e)
        {
            Log.AbortFailed(_logger, e, key.Value);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "The input is not a manifest with a provider name and a manifest name")]
        public static partial void NotAManifest(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Prepare of {Manifest} failed: {Status}")]
        public static partial void PrepareFailed(ILogger logger, Exception exception, string manifest, MethodStatus status);

        [LoggerMessage(Level = LogLevel.Error, Message = "Creating the nodes of {Manifest} failed; what was created is removed and the staged manifest aborted")]
        public static partial void NodesFailed(ILogger logger, Exception exception, string manifest);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Commit of {Manifest} failed: {Status}; the nodes of the live version are kept: {WasLive}")]
        public static partial void CommitFailed(ILogger logger, Exception exception, string manifest, MethodStatus status, bool wasLive);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manifest {Manifest} is live (accepted whole: {Accepted}) with {VariableCount} variables; replaced a live version: {Replaced}")]
        public static partial void Injected(ILogger logger, string manifest, bool accepted, int variableCount, bool replaced);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manifest {Manifest} was already live with identical content; {VariableCount} variables in place")]
        public static partial void AlreadyLive(ILogger logger, string manifest, int variableCount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Remove of {Manifest} failed: {Status}")]
        public static partial void RemoveFailed(ILogger logger, Exception exception, string manifest, MethodStatus status);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manifest {Manifest} removed (was live in the service: {Removed}); {VariableCount} variables deleted")]
        public static partial void Removed(ILogger logger, string manifest, bool removed, int variableCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilt {Manifest} from the service: {CreatedCount} variables created")]
        public static partial void Rebuilt(ILogger logger, string manifest, int createdCount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Abort of {Manifest} failed; the staged entry expires on its own")]
        public static partial void AbortFailed(ILogger logger, Exception exception, string manifest);
    }
}
