using DWIS.RealTime.Core;
using DWIS.RealTime.OpcFoundation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DWIS.RealTime.Host;

/// <summary>The settings of the host that are not Core's: where the OPC UA stack keeps its certificate stores.</summary>
public sealed class HostSettings
{
    /// <summary>The PKI root folder of the OPC UA stack (application certificate, trusted, issuers, rejected); empty for the stack's default.</summary>
    public string PkiRoot { get; set; } = "";
}

/// <summary>Starts the OPC UA server, then Core over its adapter; stops both in the reverse order.</summary>
public sealed class RealTimeService : IHostedService
{
    private readonly RealTimeOptions _options;
    private readonly HostSettings _host;
    private readonly ILoggerFactory _loggers;
    private ServerHost? _server;
    private RealTimeCore? _core;

    /// <summary>Creates the service from the configuration.</summary>
    public RealTimeService(IOptions<RealTimeOptions> options, IOptions<HostSettings> host, ILoggerFactory loggers)
    {
        _options = options.Value;
        _host = host.Value;
        _loggers = loggers;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _server = await ServerHost.StartAsync(_options.EndpointUrl, string.IsNullOrWhiteSpace(_host.PkiRoot) ? null : _host.PkiRoot, _loggers, cancellationToken).ConfigureAwait(false);
        var adapter = _server.NodeManager;
        _core = new RealTimeCore(adapter, adapter, adapter, _options, _loggers.CreateLogger<RealTimeCore>());
        _server.Server.SessionEvents = _core;
        await _core.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_core is not null)
        {
            await _core.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
