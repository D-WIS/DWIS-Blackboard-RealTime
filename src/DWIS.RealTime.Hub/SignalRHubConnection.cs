using DWIS.Blackboard.Contracts.Manifests;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Hub;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DWIS.RealTime.Hub;

/// <summary>
/// Core's <see cref="IHubConnection"/> over the old DWIS hub: SignalR over web sockets with MessagePack and automatic
/// reconnection, the four methods and the three messages of <c>DWISHub</c>, exactly as the old
/// <c>DDHubServerManager.ManageHub</c> used them (R-HUB-1).
/// </summary>
public sealed partial class SignalRHubConnection : IHubConnection
{
    private readonly HubConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Creates the connection to the hub at a base URL (the route <c>/dwishub</c> is appended); nothing connects until <see cref="StartAsync"/>.</summary>
    public SignalRHubConnection(string hubUrl, ILoggerFactory? loggers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hubUrl);
        _logger = loggers?.CreateLogger<SignalRHubConnection>() ?? NullLogger<SignalRHubConnection>.Instance;
        var builder = new HubConnectionBuilder().WithUrl(hubUrl.TrimEnd('/') + HubNames.Route).WithAutomaticReconnect().AddMessagePackProtocol();
        if (loggers is not null)
        {
            builder.ConfigureLogging(logging => logging.Services.AddSingleton(loggers));
        }
        _connection = builder.Build();
    }

    /// <inheritdoc />
    public async Task StartAsync(IHubListener listener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _connection.On<RealTimeDataDTO>(HubNames.SendSignals, dto => Guard(() => listener.SignalsReceivedAsync(ToSignals(dto), CancellationToken.None)));
        _connection.On<ManifestFileDTO>(HubNames.SendManifest, dto => Guard(() => listener.ManifestsReceivedAsync(ToManifests(dto), CancellationToken.None)));
        _connection.On<int>(HubNames.ConnectionInfo, listener.ConnectionCountChanged);
        _connection.Reconnected += _ => Guard(() => listener.ReconnectedAsync(CancellationToken.None));
        _connection.Closed += e => { Log.Closed(_logger, e?.Message ?? "closed"); return Task.CompletedTask; };
        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> RegisterToGroupAsync(string group, string password, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<bool>(HubNames.RegisterToGroup, group, password, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<HubManifests>> GetSessionManifestsAsync(CancellationToken cancellationToken)
    {
        var kept = await _connection.InvokeAsync<SessionManifestDTO?>(HubNames.GetSessionManifests, cancellationToken).ConfigureAwait(false);
        return kept?.Manifests?.Select(ToManifests).ToList() ?? [];
    }

    /// <inheritdoc />
    public Task<int> GetConnectionCountAsync(CancellationToken cancellationToken) =>
        _connection.InvokeAsync<int>(HubNames.GetConnectionCount, cancellationToken);

    /// <inheritdoc />
    public Task SendSignalsAsync(HubSignals signals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var dto = new RealTimeDataDTO { Sender = signals.Sender, NodeIDs = signals.Batch.NodeIds, Attributes = signals.Batch.Attributes, Values = signals.Batch.Values, Namespaces = signals.Batch.Namespaces };
        return _connection.SendAsync(HubNames.SendSignals, dto, cancellationToken);
    }

    /// <inheritdoc />
    public Task SendManifestsAsync(HubManifests manifests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var dto = new ManifestFileDTO { Sender = manifests.Sender, ManifestFiles = manifests.ManifestJsons.Select(ManifestJson.Parse).ToArray() };
        return _connection.SendAsync(HubNames.SendManifest, dto, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static HubSignals ToSignals(RealTimeDataDTO dto) =>
        new(dto.Sender, new ReplicationBatch(dto.NodeIDs ?? [], dto.Values ?? [], dto.Attributes ?? [], dto.Namespaces ?? []));

    private static HubManifests ToManifests(ManifestFileDTO dto) =>
        new(dto.Sender, (dto.ManifestFiles ?? []).Select(ManifestJson.Serialize).ToList());

    private async Task Guard(Func<Task> handler)
    {
        try
        {
            await handler().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.HandlerFailed(_logger, e);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Hub connection closed: {Reason}")]
        public static partial void Closed(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A message from the hub could not be handled")]
        public static partial void HandlerFailed(ILogger logger, Exception exception);
    }
}
