using DWIS.Blackboard.Contracts.Grpc;
using DWIS.Blackboard.Contracts.Manifests;
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
            CreatedNodes addresses;
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
                _live.Set(new LiveManifest(key, prepared.ContentHash, addresses.VariableIds));
                Log.AlreadyLive(_logger, key.Value, addresses.Variables.Count);
                return new Outcome(MethodStatus.Good, InjectionResultJson.Success(key.Provider, addresses.Declared(manifest)));
            }

            var commit = new CommitRequest { Manifest = key.ToRef() };
            commit.Endpoints.AddRange(addresses.All.Select(a => new VariableEndpoint { VariableId = a.VariableId, EndpointUrl = a.Address.EndpointUrl, NamespaceUri = a.Address.NamespaceUri, NodeId = a.Address.NodeId }));
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

            var current = addresses.VariableIds;
            if (previous is not null)
            {
                RemoveNodes(key.Provider, previous.VariableIds.Except(current).ToList(), false);
            }
            _live.Set(new LiveManifest(key, prepared.ContentHash, current));
            Log.Injected(_logger, key.Value, committed.Accepted, current.Count, previous is not null);
            return new Outcome(MethodStatus.Good, InjectionResultJson.Success(key.Provider, addresses.Declared(manifest)));
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
                _live.Set(new LiveManifest(key, summary.ContentHash, addresses.VariableIds));
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

    /// <summary>
    /// The nodes made for a manifest: the variables, each created with its validity boolean by the adapter, and the
    /// validity booleans themselves, which the service lists as variables of their own (an entry whose id is another
    /// entry's validity id, with no validity id of its own) and wants an endpoint for.
    /// </summary>
    private sealed record CreatedNodes(List<(string VariableId, NodeAddress Address)> Variables, List<(string VariableId, NodeAddress Address)> Flags)
    {
        /// <summary>The ids of the variables (not the booleans): what the live table keeps and what a delete removes.</summary>
        public List<string> VariableIds => Variables.Select(v => v.VariableId).ToList();

        /// <summary>Variables and booleans: what the service gets as endpoints.</summary>
        public IEnumerable<(string VariableId, NodeAddress Address)> All => Variables.Concat(Flags);

        /// <summary>
        /// The mappings the client is answered: one per variable the manifest declares, in manifest order, as the old
        /// server did. A boolean the client put in the manifest itself (the old client library did) is listed; one the
        /// service added is not.
        /// </summary>
        public List<(string VariableId, NodeAddress Address)> Declared(Manifest manifest)
        {
            // The service names a variable by its safe id (decision D-62); the result names it as the manifest wrote it.
            // A manifest may list one id twice (the old server's own CLI example does): one node, one mapping per entry.
            var byId = new Dictionary<string, NodeAddress>(StringComparer.Ordinal);
            foreach (var (variableId, address) in All)
            {
                byId.TryAdd(variableId, address);
            }
            return manifest.ProvidedVariables.Where(v => byId.ContainsKey(Naming.Safe(v.VariableID))).Select(v => (v.VariableID, byId[Naming.Safe(v.VariableID)])).ToList();
        }
    }

    private CreatedNodes CreateNodes(string provider, IEnumerable<Blackboard.Contracts.Grpc.ProvidedVariable> variables, IReadOnlyList<string>? existing, List<string> created)
    {
        _space.CreateProviderFolder(provider);
        var list = variables.ToList();
        var flagIds = list.Where(v => !string.IsNullOrEmpty(v.ValidityVariableId)).Select(v => v.ValidityVariableId).ToHashSet(StringComparer.Ordinal);
        var addresses = new List<(string, NodeAddress)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in list.Where(v => !flagIds.Contains(v.VariableId)))
        {
            if (!seen.Add(variable.VariableId))
            {
                continue; // listed twice in the manifest: one node, created once
            }
            var spec = new VariableSpec(variable.VariableId, variable.DataType, variable.Rank, [.. variable.Dimensions], variable.InitialValue,
                string.IsNullOrEmpty(variable.ValidityVariableId) ? variable.VariableId + Naming.ValiditySuffix : variable.ValidityVariableId);
            var address = _space.CreateVariable(provider, spec);
            if (existing is null || !existing.Contains(variable.VariableId))
            {
                created.Add(variable.VariableId);
            }
            addresses.Add((variable.VariableId, address));
        }
        var flags = list.Where(v => flagIds.Contains(v.VariableId)).DistinctBy(v => v.VariableId, StringComparer.Ordinal).Select(v => (v.VariableId, _space.AddressFor(provider, v.VariableId))).ToList();
        return new CreatedNodes(addresses, flags);
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
