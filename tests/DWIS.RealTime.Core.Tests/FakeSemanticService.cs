using System.Security.Cryptography;
using System.Text;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Injection;
using DWIS.RealTime.Core.Semantic;

namespace DWIS.RealTime.Core.Tests;

/// <summary>
/// A semantic service in memory with the contract's behaviour for the injection side: prepare stages the manifest
/// and answers its variables with validity ids, identical live content answers <c>already_live</c>, commit makes
/// it live and keeps the endpoints, remove and list do what they say. Switches make it fail where a test wants.
/// </summary>
public sealed class FakeSemanticService : ISemanticService
{
    private readonly Dictionary<string, (Manifest Manifest, string Hash)> _staged = [];

    /// <summary>The live manifests by id: the manifest, its content hash and the endpoints committed with it.</summary>
    public Dictionary<string, (Manifest Manifest, string Hash, List<VariableEndpoint> Endpoints)> Live { get; } = [];

    /// <summary>The calls made, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>When set, every call throws with this status (the service is down).</summary>
    public MethodStatus? Down { get; set; }

    /// <summary>When set, the next commit throws with this status, then the switch clears.</summary>
    public MethodStatus? FailNextCommit { get; set; }

    /// <summary>When true, prepare refuses the manifest as invalid.</summary>
    public bool RefusePrepare { get; set; }

    private void Check(string call)
    {
        Calls.Add(call);
        if (Down is { } status)
        {
            throw new SemanticException(status, $"{call}: the service is down");
        }
    }

    /// <inheritdoc />
    public Task<PrepareResult> PrepareAsync(PrepareRequest request, CancellationToken cancellationToken)
    {
        Check("prepare");
        if (RefusePrepare)
        {
            throw new SemanticException(MethodStatus.BadInvalidArgument, "prepare: refused");
        }
        var manifest = ManifestJson.Parse(request.ManifestJson);
        var key = ManifestKey.Of(manifest) ?? throw new SemanticException(MethodStatus.BadInvalidArgument, "A manifest needs a provider name and a manifest name.");
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.ManifestJson)));
        var alreadyLive = Live.TryGetValue(key.Value, out var live) && live.Hash == hash;
        if (!alreadyLive)
        {
            _staged[key.Value] = (manifest, hash);
        }
        var result = new PrepareResult { Manifest = key.ToRef(), Accepted = true, ReportJson = "{}", ContentHash = hash, AlreadyLive = alreadyLive };
        result.Variables.AddRange(VariablesOf(manifest));
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken cancellationToken)
    {
        Check("commit");
        if (FailNextCommit is { } status)
        {
            FailNextCommit = null;
            throw new SemanticException(status, "commit: failed on purpose");
        }
        var key = ManifestKey.Of(request.Manifest);
        if (!_staged.Remove(key.Value, out var staged))
        {
            throw new SemanticException(MethodStatus.BadInvalidArgument, $"commit: {key.Value} is not staged");
        }
        Live[key.Value] = (staged.Manifest, staged.Hash, [.. request.Endpoints]);
        return Task.FromResult(new CommitResult { Manifest = request.Manifest, Accepted = true, ReportJson = "{}" });
    }

    /// <inheritdoc />
    public Task AbortAsync(ManifestRef manifest, CancellationToken cancellationToken)
    {
        Check("abort");
        _staged.Remove(ManifestKey.Of(manifest).Value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(ManifestRef manifest, CancellationToken cancellationToken)
    {
        Check("remove");
        return Task.FromResult(Live.Remove(ManifestKey.Of(manifest).Value));
    }

    /// <inheritdoc />
    public Task<ManifestList> ListAsync(CancellationToken cancellationToken)
    {
        Check("list");
        var list = new ManifestList();
        foreach (var (id, (manifest, hash, endpoints)) in Live)
        {
            var key = ManifestKey.Of(manifest)!;
            var summary = new ManifestSummary { Manifest = key.ToRef(), ManifestName = manifest.ManifestName, Accepted = true, ContentHash = hash, VocabularyVersion = "test" };
            summary.Variables.AddRange(VariablesOf(manifest));
            summary.Endpoints.AddRange(endpoints);
            list.Manifests.Add(summary);
            _ = id;
        }
        return Task.FromResult(list);
    }

    /// <summary>The variables to create for a manifest, as the service's pipeline would answer them.</summary>
    public static IEnumerable<Blackboard.Contracts.Grpc.ProvidedVariable> VariablesOf(Manifest manifest) =>
        manifest.ProvidedVariables.Select(v =>
        {
            var variable = new Blackboard.Contracts.Grpc.ProvidedVariable { VariableId = v.VariableID, DataType = v.DataType ?? "double", Rank = v.Rank, InitialValue = v.InitialValue ?? "", ValidityVariableId = v.VariableID + Naming.ValiditySuffix };
            variable.Dimensions.AddRange(v.Dimensions ?? []);
            return variable;
        });
}
