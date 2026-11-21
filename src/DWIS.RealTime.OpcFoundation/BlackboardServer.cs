using System.Reflection;
using DWIS.RealTime.Core.Adapters;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;

namespace DWIS.RealTime.OpcFoundation;

/// <summary>
/// The OPC Foundation server of the real-time process: one <see cref="BlackboardNodeManager"/>, and the session
/// hooks that feed Core's <see cref="ISessionEvents"/> (decision D-54).
/// </summary>
public sealed class BlackboardServer : StandardServer
{
    private readonly string _endpointUrl;
    private readonly ILoggerFactory? _loggers;

    /// <summary>Creates the server for an endpoint URL (what the node manager reports in every address).</summary>
    public BlackboardServer(string endpointUrl, ILoggerFactory? loggers = null)
    {
        _endpointUrl = endpointUrl;
        _loggers = loggers;
    }

    /// <summary>The node manager, the adapter: set once the server starts.</summary>
    public BlackboardNodeManager? NodeManager { get; private set; }

    /// <summary>Who hears about sessions (Core). Set before the server starts, or whenever.</summary>
    public ISessionEvents? SessionEvents { get; set; }

    /// <inheritdoc />
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        NodeManager = new BlackboardNodeManager(server, configuration, _endpointUrl, _loggers?.CreateLogger<BlackboardNodeManager>());
        return new MasterNodeManager(server, configuration, null, NodeManager);
    }

    /// <inheritdoc />
    protected override void OnServerStarted(IServerInternal server)
    {
        base.OnServerStarted(server);
        server.SessionManager.SessionCreated += (session, _) => SessionEvents?.SessionOpened(new SessionId(session.Id.ToString()));
        server.SessionManager.SessionClosing += (session, _) => SessionEvents?.SessionClosed(new SessionId(session.Id.ToString()));
    }

    /// <inheritdoc />
    protected override ServerProperties LoadServerProperties()
    {
        var version = typeof(BlackboardServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        return new ServerProperties
        {
            ManufacturerName = "D-WIS",
            ProductName = "DWIS Blackboard Rig Edition",
            ProductUri = "https://github.com/D-WIS/DWIS-Blackboard-RealTime",
            SoftwareVersion = version,
            BuildNumber = version,
            BuildDate = DateTime.UtcNow,
        };
    }
}
