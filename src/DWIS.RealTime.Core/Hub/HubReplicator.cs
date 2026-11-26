using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Injection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DWIS.RealTime.Core.Hub;

/// <summary>
/// The hub role (specification section 6, R-HUB-1 to R-HUB-3, decision D-63), ported from the old
/// <c>DDHubServerManager.ManageHub</c>: connects to the hub, registers to the group, takes the manifests the hub
/// kept, then every batch period sends the values clients wrote since (the latest per variable) and the manifests
/// that went live here; writes a peer's values to the local variables of that provider and injects a peer's
/// manifests through the injection path with the origin <c>hub:{group}:{sender}</c>. Its own messages come back
/// from the hub and are dropped by the sender id. Nothing here touches the value path of a client (R-RT-1).
/// </summary>
public sealed partial class HubReplicator : IHubListener, IAsyncDisposable
{
    private readonly IHubConnection _hub;
    private readonly IReplicationCodec _codec;
    private readonly IAddressSpace _space;
    private readonly Injector _injector;
    private readonly HubOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Provider, string VariableId), ReplicatedValue> _pendingValues = [];
    private readonly List<string> _pendingManifests = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _run;
    private volatile bool _canWrite;

    /// <summary>Creates the replicator over the hub connection, the adapter's codec and address space, and the injector.</summary>
    public HubReplicator(IHubConnection hub, IReplicationCodec codec, IAddressSpace space, Injector injector, HubOptions options, ILogger<HubReplicator>? logger = null)
    {
        _hub = hub;
        _codec = codec;
        _space = space;
        _injector = injector;
        _options = options;
        _logger = logger ?? NullLogger<HubReplicator>.Instance;
        injector.ManifestLive += ManifestLive;
    }

    /// <summary>This process on the hub: the sender id of its messages, new at every start as the old server's was.</summary>
    public Guid Sender { get; } = Guid.NewGuid();

    /// <summary>True once the hub registered this process to its group with write rights.</summary>
    public bool CanWrite => _canWrite;

    /// <summary>True once the connection, the registration and the session manifests are done.</summary>
    public bool Connected { get; private set; }

    /// <summary>Counters for the diagnostics and the tests.</summary>
    public int BatchesSent { get; private set; }

    /// <inheritdoc cref="BatchesSent"/>
    public int ManifestsSent { get; private set; }

    /// <inheritdoc cref="BatchesSent"/>
    public int ValuesReceived { get; private set; }

    /// <inheritdoc cref="BatchesSent"/>
    public int ManifestsReceived { get; private set; }

    /// <summary>The origin a manifest from a peer of the group carries in the provenance graph (R-HUB-3).</summary>
    public string OriginOf(Guid sender) => $"hub:{_options.Group}:{sender}";

    /// <summary>Starts the role in the background: connects (again every <paramref name="retry"/> until the hub answers), then runs the send loop until stopped.</summary>
    public void Start(TimeSpan retry)
    {
        _run ??= RunAsync(retry, _stopping.Token);
    }

    /// <summary>Connects, registers, takes the session manifests and runs the send loop; throws when the hub cannot be reached.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _hub.StartAsync(this, cancellationToken).ConfigureAwait(false);
        _canWrite = await _hub.RegisterToGroupAsync(_options.Group, _options.Password, cancellationToken).ConfigureAwait(false);
        Log.Registered(_logger, _options.Url, _options.Group, _options.Site, _canWrite);
        var kept = await _hub.GetSessionManifestsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var manifests in kept)
        {
            await ManifestsReceivedAsync(manifests, cancellationToken).ConfigureAwait(false);
        }
        var count = await _hub.GetConnectionCountAsync(cancellationToken).ConfigureAwait(false);
        Log.Connections(_logger, count);
        Connected = true;
    }

    /// <summary>Buffers a client write for the next batch (called by Core as <see cref="IValueEvents"/>); the latest value per variable wins.</summary>
    public void ValueWritten(string provider, string variableId, object? value, DateTime sourceTimestamp)
    {
        if (!_canWrite)
        {
            return;
        }
        var address = _space.AddressFor(provider, variableId);
        lock (_gate)
        {
            _pendingValues[(provider, variableId)] = new ReplicatedValue(address.NamespaceUri, address.NodeId, ReplicatedValue.ValueAttribute, value, sourceTimestamp);
        }
    }

    /// <summary>Sends what is buffered now: one batch of values, then the manifests. Returns what went out.</summary>
    public async Task<(int Values, int Manifests)> FlushAsync(CancellationToken cancellationToken)
    {
        List<ReplicatedValue> values;
        List<string> manifests;
        lock (_gate)
        {
            values = [.. _pendingValues.Values];
            _pendingValues.Clear();
            manifests = [.. _pendingManifests];
            _pendingManifests.Clear();
        }
        if (values.Count > 0)
        {
            await _hub.SendSignalsAsync(new HubSignals(Sender, _codec.Encode(values)), cancellationToken).ConfigureAwait(false);
            BatchesSent++;
        }
        if (manifests.Count > 0)
        {
            await _hub.SendManifestsAsync(new HubManifests(Sender, manifests), cancellationToken).ConfigureAwait(false);
            ManifestsSent += manifests.Count;
            Log.ManifestsSent(_logger, manifests.Count);
        }
        return (values.Count, manifests.Count);
    }

    /// <inheritdoc />
    public async Task SignalsReceivedAsync(HubSignals signals, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (signals.Sender == Sender)
        {
            return;
        }
        IReadOnlyList<ReplicatedValue> values;
        try
        {
            values = _codec.Decode(signals.Batch);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.BatchUnreadable(_logger, e, signals.Sender);
            return;
        }
        foreach (var value in values)
        {
            if (value.AttributeId != ReplicatedValue.ValueAttribute || Naming.VariableOf(value.NamespaceUri, value.NodeId) is not { } variable)
            {
                continue;
            }
            if (_space.ReadValue(variable.Provider, variable.VariableId) is null)
            {
                Log.UnknownVariable(_logger, value.NamespaceUri, value.NodeId);
                continue;
            }
            _space.WriteValue(variable.Provider, variable.VariableId, value.Value, value.SourceTimestamp);
            ValuesReceived++;
        }
    }

    /// <inheritdoc />
    public async Task ManifestsReceivedAsync(HubManifests manifests, CancellationToken cancellationToken)
    {
        if (manifests.Sender == Sender)
        {
            return;
        }
        var origin = OriginOf(manifests.Sender);
        foreach (var json in manifests.ManifestJsons)
        {
            var outcome = await _injector.InjectAsync(json, origin, cancellationToken).ConfigureAwait(false);
            ManifestsReceived++;
            Log.ManifestReceived(_logger, origin, outcome.Status);
        }
    }

    /// <inheritdoc />
    public void ConnectionCountChanged(int count) => Log.Connections(_logger, count);

    /// <inheritdoc />
    public async Task ReconnectedAsync(CancellationToken cancellationToken)
    {
        _canWrite = await _hub.RegisterToGroupAsync(_options.Group, _options.Password, cancellationToken).ConfigureAwait(false);
        Log.Registered(_logger, _options.Url, _options.Group, _options.Site, _canWrite);
    }

    /// <summary>Stops the loop and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        _injector.ManifestLive -= ManifestLive;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try { await _run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await _hub.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private void ManifestLive(string manifestJson, string origin)
    {
        // A manifest that came from the hub is not sent back; a local one goes out with the next batch.
        if (origin.StartsWith("hub:", StringComparison.Ordinal))
        {
            return;
        }
        lock (_gate)
        {
            _pendingManifests.Add(manifestJson);
        }
    }

    private async Task RunAsync(TimeSpan retry, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.ConnectFailed(_logger, e, _options.Url, retry.TotalSeconds);
                try { await Task.Delay(retry, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(20, _options.BatchMilliseconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_canWrite)
                {
                    lock (_gate) { _pendingValues.Clear(); }
                    continue;
                }
                try
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log.SendFailed(_logger, e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Hub {Url}: registered to group {Group} as site {Site}; write rights: {CanWrite}")]
        public static partial void Registered(ILogger logger, string url, string group, string site, bool canWrite);

        [LoggerMessage(Level = LogLevel.Information, Message = "Hub group has {Count} connection(s)")]
        public static partial void Connections(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Hub {Url} not reached; trying again in {Seconds} s")]
        public static partial void ConnectFailed(ILogger logger, Exception exception, string url, double seconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sending to the hub failed; the next batch tries again")]
        public static partial void SendFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "{Count} manifest(s) sent to the hub")]
        public static partial void ManifestsSent(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Manifest from {Origin} injected: {Status}")]
        public static partial void ManifestReceived(ILogger logger, string origin, MethodStatus status);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A batch of values from {Sender} could not be read")]
        public static partial void BatchUnreadable(ILogger logger, Exception exception, Guid sender);

        [LoggerMessage(Level = LogLevel.Debug, Message = "A peer's value for {NamespaceUri} {NodeId} has no variable here; dropped")]
        public static partial void UnknownVariable(ILogger logger, string namespaceUri, string nodeId);
    }
}
