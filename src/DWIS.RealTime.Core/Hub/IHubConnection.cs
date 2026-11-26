using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Hub;

/// <summary>
/// The connection to the hub as Core sees it (decision D-63): the four calls and the three messages of the old
/// <c>DWISHub</c> (<c>RegisterToGroup</c>, <c>GetSessionManifests</c>, <c>GetConnectionCount</c>, <c>SendSignals</c>,
/// <c>SendManifest</c>, <c>ConnectionInfo</c>), without any SignalR type. The SignalR client with MessagePack
/// implements it in <c>DWIS.RealTime.Hub</c>; the tests have a fake.
/// </summary>
public interface IHubConnection : IAsyncDisposable
{
    /// <summary>Connects to the hub (with automatic reconnection) and routes what the hub sends to the listener.</summary>
    Task StartAsync(IHubListener listener, CancellationToken cancellationToken);

    /// <summary>Registers to a group with its password; true when this process may write to the group.</summary>
    Task<bool> RegisterToGroupAsync(string group, string password, CancellationToken cancellationToken);

    /// <summary>The manifests the hub keeps for the group's session, sent by the peers before this process connected.</summary>
    Task<IReadOnlyList<HubManifests>> GetSessionManifestsAsync(CancellationToken cancellationToken);

    /// <summary>How many connections the group has, this one included.</summary>
    Task<int> GetConnectionCountAsync(CancellationToken cancellationToken);

    /// <summary>Sends a batch of values to the other members of the group.</summary>
    Task SendSignalsAsync(HubSignals signals, CancellationToken cancellationToken);

    /// <summary>Sends manifests to the other members of the group; the hub keeps them for members that join later.</summary>
    Task SendManifestsAsync(HubManifests manifests, CancellationToken cancellationToken);
}

/// <summary>What the hub sends to a member: implemented by Core's replicator.</summary>
public interface IHubListener
{
    /// <summary>A peer's values (the old <c>SendSignals</c> message).</summary>
    Task SignalsReceivedAsync(HubSignals signals, CancellationToken cancellationToken);

    /// <summary>A peer's manifests (the old <c>SendManifest</c> message).</summary>
    Task ManifestsReceivedAsync(HubManifests manifests, CancellationToken cancellationToken);

    /// <summary>The group's connection count changed (the old <c>ConnectionInfo</c> message).</summary>
    void ConnectionCountChanged(int count);

    /// <summary>The connection came back after a drop: the member registers to its group again.</summary>
    Task ReconnectedAsync(CancellationToken cancellationToken);
}

/// <summary>A batch of values on the hub: who sent it, and the wire fields.</summary>
public sealed record HubSignals(Guid Sender, ReplicationBatch Batch);

/// <summary>Manifests on the hub: who sent them, and their JSON as the senders wrote them.</summary>
public sealed record HubManifests(Guid Sender, IReadOnlyList<string> ManifestJsons);
