using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.Blackboard.Contracts.Manifests;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Injection;
using DWIS.RealTime.Core.Semantic;

namespace DWIS.RealTime.Core.Tests;

/// <summary>
/// A semantic service in memory with the contract's behaviour: prepare stages the manifest and answers its
/// variables with validity ids, identical live content answers <c>already_live</c>, commit makes it live and keeps
/// the endpoints, remove and list do what they say; queries are registered per owner with ids the test sets the
/// answers of, and the watch stream is a channel the test pushes diffs into or ends. Switches make it fail where
/// a test wants.
/// </summary>
public sealed class FakeSemanticService : ISemanticService
{
    private readonly Dictionary<string, (Manifest Manifest, string Hash)> _staged = [];
    private readonly Dictionary<string, int> _queryIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _owners = [];
    private Channel<QueryDiff> _stream = Channel.CreateUnbounded<QueryDiff>();
    private int _nextQueryId;

    /// <summary>The live manifests by id: the manifest, its content hash and the endpoints committed with it.</summary>
    public Dictionary<string, (Manifest Manifest, string Hash, List<VariableEndpoint> Endpoints)> Live { get; } = [];

    /// <summary>The answer the service gives a query text (variables and rows); a text without one answers one row of itself.</summary>
    public Dictionary<string, (string[] Variables, List<Row> Rows)> Answers { get; } = new(StringComparer.Ordinal);

    /// <summary>The individuals and sentences <c>GetResources</c> answers.</summary>
    public ResourceFile Resources { get; } = new();

    /// <summary>The calls made, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>When set, every call throws with this status (the service is down).</summary>
    public MethodStatus? Down { get; set; }

    /// <summary>When set, the next commit throws with this status, then the switch clears.</summary>
    public MethodStatus? FailNextCommit { get; set; }

    /// <summary>When true, prepare refuses the manifest as invalid.</summary>
    public bool RefusePrepare { get; set; }

    /// <summary>The owners of a query by its id.</summary>
    public IReadOnlyDictionary<int, HashSet<string>> Owners => _owners;

    /// <summary>The id of a registered query text, or 0.</summary>
    public int IdOf(string text) => _queryIds.GetValueOrDefault(text);

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
        foreach (var (manifest, hash, endpoints) in Live.Values)
        {
            var key = ManifestKey.Of(manifest)!;
            var summary = new ManifestSummary { Manifest = key.ToRef(), ManifestName = manifest.ManifestName, Accepted = true, ContentHash = hash, VocabularyVersion = "test" };
            summary.Variables.AddRange(VariablesOf(manifest));
            summary.Endpoints.AddRange(endpoints);
            list.Manifests.Add(summary);
        }
        return Task.FromResult(list);
    }

    /// <inheritdoc />
    public Task<QueryResult> QueryAsync(string sparql, CancellationToken cancellationToken)
    {
        Check("query");
        var (variables, rows) = AnswerOf(sparql);
        var result = new QueryResult();
        result.Variables.AddRange(variables);
        result.Rows.AddRange(rows);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<ResourceFile> GetResourcesAsync(CancellationToken cancellationToken)
    {
        Check("resources");
        return Task.FromResult(Resources);
    }

    /// <inheritdoc />
    public Task<QuerySnapshot> RegisterQueryAsync(string sparql, string owner, CancellationToken cancellationToken)
    {
        Check("register");
        if (!_queryIds.TryGetValue(sparql, out var id))
        {
            id = ++_nextQueryId;
            _queryIds[sparql] = id;
            _owners[id] = [];
        }
        _owners[id].Add(owner);
        var (variables, rows) = AnswerOf(sparql);
        var snapshot = new QuerySnapshot { QueryId = id };
        snapshot.Variables.AddRange(variables);
        snapshot.Rows.AddRange(rows);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task<UnregisterResult> UnregisterQueryAsync(string sparql, string owner, CancellationToken cancellationToken)
    {
        Check("unregister");
        if (!_queryIds.TryGetValue(sparql, out var id))
        {
            return Task.FromResult(new UnregisterResult { Released = false, QueryId = 0 });
        }
        var released = _owners[id].Remove(owner);
        if (_owners[id].Count == 0)
        {
            _owners.Remove(id);
            _queryIds.Remove(sparql);
        }
        return Task.FromResult(new UnregisterResult { Released = released, QueryId = id });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> ReleaseOwnerAsync(string owner, CancellationToken cancellationToken)
    {
        Check("release");
        var gone = new List<int>();
        foreach (var (id, owners) in _owners.ToList())
        {
            if (owners.Remove(owner) && owners.Count == 0)
            {
                _owners.Remove(id);
                _queryIds.Remove(_queryIds.First(p => p.Value == id).Key);
                gone.Add(id);
            }
        }
        return Task.FromResult<IReadOnlyList<int>>(gone);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryDiff> WatchAsync(string watcher, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Check("watch");
        var stream = _stream;
        await foreach (var diff in stream.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return diff;
        }
    }

    /// <summary>Pushes a diff of a query (by its service id) into the stream.</summary>
    public void Push(int queryId, IEnumerable<Row> added, IEnumerable<Row> removed)
    {
        var diff = new QueryDiff { QueryId = queryId };
        diff.Added.AddRange(added);
        diff.Removed.AddRange(removed);
        _stream.Writer.TryWrite(diff);
    }

    /// <summary>Ends the stream as a service restart would: the registrations are gone, the ids start again, a new stream waits.</summary>
    public void Restart()
    {
        var old = _stream;
        _stream = Channel.CreateUnbounded<QueryDiff>();
        _queryIds.Clear();
        _owners.Clear();
        _nextQueryId = 100;
        old.Writer.TryComplete();
    }

    private (string[] Variables, List<Row> Rows) AnswerOf(string sparql) =>
        Answers.TryGetValue(sparql, out var answer) ? answer : (["s"], [RowOf("http://d-wis.org/Fake/" + sparql.Length)]);

    /// <summary>A row of values.</summary>
    public static Row RowOf(params string[] values)
    {
        var row = new Row();
        row.Values.AddRange(values);
        return row;
    }

    /// <summary>
    /// The variables to create for a manifest, as the service's pipeline answers them (its <c>ManifestPreparation</c>):
    /// the declared variables in manifest order, each with the id of its validity boolean, then the booleans the
    /// service added, one per variable that had none, as entries of their own with no validity id. A boolean the
    /// manifest declares itself (the old client library put them in) is left as declared, with no validity id.
    /// </summary>
    public static IEnumerable<Blackboard.Contracts.Grpc.ProvidedVariable> VariablesOf(Manifest manifest)
    {
        var ids = manifest.ProvidedVariables.Select(v => v.VariableID).ToHashSet(StringComparer.Ordinal);
        bool IsFlag(string id) => id.EndsWith(Naming.ValiditySuffix, StringComparison.Ordinal) && ids.Contains(id[..^Naming.ValiditySuffix.Length]);
        var declared = manifest.ProvidedVariables.Select(v =>
        {
            // The service names a variable by its safe id (decision D-62), as it reads it back from the IRI.
            var variable = new Blackboard.Contracts.Grpc.ProvidedVariable { VariableId = Naming.Safe(v.VariableID), DataType = v.DataType ?? "double", Rank = v.Rank, InitialValue = v.InitialValue ?? "", ValidityVariableId = IsFlag(v.VariableID) ? "" : Naming.Safe(v.VariableID) + Naming.ValiditySuffix };
            variable.Dimensions.AddRange(v.Dimensions ?? []);
            return variable;
        });
        var added = manifest.ProvidedVariables.Where(v => !IsFlag(v.VariableID) && !ids.Contains(v.VariableID + Naming.ValiditySuffix))
            .Select(v => new Blackboard.Contracts.Grpc.ProvidedVariable { VariableId = Naming.Safe(v.VariableID) + Naming.ValiditySuffix, DataType = "bool", Rank = 0, InitialValue = "true", ValidityVariableId = "" });
        return declared.Concat(added);
    }
}
