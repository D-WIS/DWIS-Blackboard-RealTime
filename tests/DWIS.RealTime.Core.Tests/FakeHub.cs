using System.Text.Json;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Hub;

namespace DWIS.RealTime.Core.Tests;

/// <summary>A hub connection in memory: records what Core sends, and lets a test push what a peer would send.</summary>
public sealed class FakeHubConnection : IHubConnection
{
    private IHubListener? _listener;

    /// <summary>Every batch of values sent, in order.</summary>
    public List<HubSignals> SignalsSent { get; } = [];

    /// <summary>Every manifest message sent, in order.</summary>
    public List<HubManifests> ManifestsSent { get; } = [];

    /// <summary>What <c>GetSessionManifests</c> answers: the manifests the hub kept before this member joined.</summary>
    public List<HubManifests> Kept { get; } = [];

    /// <summary>The answer of <c>RegisterToGroup</c>.</summary>
    public bool WriteRights { get; set; } = true;

    /// <summary>The registrations made: group and password.</summary>
    public List<(string Group, string Password)> Registrations { get; } = [];

    /// <summary>True once Core started the connection.</summary>
    public bool Started { get; private set; }

    /// <summary>When set, <see cref="StartAsync"/> throws (the hub is down).</summary>
    public bool Down { get; set; }

    /// <inheritdoc />
    public Task StartAsync(IHubListener listener, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new InvalidOperationException("hub down");
        }
        _listener = listener;
        Started = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RegisterToGroupAsync(string group, string password, CancellationToken cancellationToken)
    {
        Registrations.Add((group, password));
        return Task.FromResult(WriteRights);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HubManifests>> GetSessionManifestsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HubManifests>>([.. Kept]);

    /// <inheritdoc />
    public Task<int> GetConnectionCountAsync(CancellationToken cancellationToken) => Task.FromResult(2);

    /// <inheritdoc />
    public Task SendSignalsAsync(HubSignals signals, CancellationToken cancellationToken)
    {
        SignalsSent.Add(signals);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendManifestsAsync(HubManifests manifests, CancellationToken cancellationToken)
    {
        ManifestsSent.Add(manifests);
        return Task.CompletedTask;
    }

    /// <summary>A peer's batch arrives.</summary>
    public Task PeerSendsAsync(HubSignals signals) => _listener!.SignalsReceivedAsync(signals, CancellationToken.None);

    /// <summary>A peer's manifests arrive.</summary>
    public Task PeerSendsAsync(HubManifests manifests) => _listener!.ManifestsReceivedAsync(manifests, CancellationToken.None);

    /// <summary>The connection came back.</summary>
    public Task ReconnectAsync() => _listener!.ReconnectedAsync(CancellationToken.None);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A codec that writes the values as JSON into the node id field, so that a test can read a batch back.</summary>
public sealed class FakeCodec : IReplicationCodec
{
    private sealed record Wire(string NamespaceUri, string NodeId, uint AttributeId, JsonElement Value, DateTime SourceTimestamp);

    /// <inheritdoc />
    public ReplicationBatch Encode(IReadOnlyList<ReplicatedValue> values)
    {
        var wire = values.Select(v => new Wire(v.NamespaceUri, v.NodeId, v.AttributeId, JsonSerializer.SerializeToElement(v.Value), v.SourceTimestamp)).ToList();
        return new ReplicationBatch(JsonSerializer.SerializeToUtf8Bytes(wire), [], values.Select(v => v.AttributeId).ToArray(), ["http://opcfoundation.org/UA/"]);
    }

    /// <inheritdoc />
    public IReadOnlyList<ReplicatedValue> Decode(ReplicationBatch batch)
    {
        var wire = JsonSerializer.Deserialize<List<Wire>>(batch.NodeIds) ?? [];
        return wire.Select(w => new ReplicatedValue(w.NamespaceUri, w.NodeId, w.AttributeId, w.Value.ValueKind == JsonValueKind.Number ? w.Value.GetDouble() : w.Value.ToString(), w.SourceTimestamp)).ToList();
    }

    /// <summary>The values of a batch this codec encoded.</summary>
    public static IReadOnlyList<ReplicatedValue> ValuesOf(HubSignals signals) => new FakeCodec().Decode(signals.Batch);
}
