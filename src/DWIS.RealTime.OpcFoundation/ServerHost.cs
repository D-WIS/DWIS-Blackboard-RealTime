using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace DWIS.RealTime.OpcFoundation;

/// <summary>
/// Starts and stops a <see cref="BlackboardServer"/> with the lab profile (Appendix E, T-SEC-5): a <c>None</c>
/// endpoint and anonymous users, so that today's clients and the samples connect unchanged; the production
/// profile comes with milestone 5. The host and the tests share this.
/// </summary>
public sealed partial class ServerHost : IAsyncDisposable
{
    private readonly ApplicationInstance _application;

    private ServerHost(ApplicationInstance application, BlackboardServer server)
    {
        _application = application;
        Server = server;
    }

    /// <summary>The running server.</summary>
    public BlackboardServer Server { get; }

    /// <summary>The adapter of the running server.</summary>
    public BlackboardNodeManager NodeManager => Server.NodeManager ?? throw new InvalidOperationException("The server has not started.");

    /// <summary>
    /// Builds the application configuration in code (no XML file), with the lab profile, and starts the server.
    /// </summary>
    /// <param name="endpointUrl">The one base address, for example <c>opc.tcp://localhost:48030</c>.</param>
    /// <param name="pkiRoot">Where the application certificate and the stores live; null for the stack's default folder.</param>
    /// <param name="loggers">For the server's own logs; null for none.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<ServerHost> StartAsync(string endpointUrl, string? pkiRoot = null, ILoggerFactory? loggers = null, CancellationToken cancellationToken = default)
    {
        // The stack's 1.5.378 line marks the plain constructor and the string security configuration obsolete in favour of a
        // telemetry context and certificate identifier lists; both still work and are what the old server uses. Moving to
        // the new forms is a chore for M5 with the production profile.
#pragma warning disable CS0618
        var application = new ApplicationInstance
        {
            ApplicationName = "DWIS Blackboard Rig Edition",
            ApplicationType = ApplicationType.Server,
        };
        var configuration = await application.Build("urn:d-wis:blackboard:realtime", "https://github.com/D-WIS/DWIS-Blackboard-RealTime")
            .AsServer([endpointUrl])
            .AddUnsecurePolicyNone()
            .AddUserTokenPolicy(UserTokenType.Anonymous)
            .AddSecurityConfiguration("CN=DWIS Blackboard Rig Edition, O=D-WIS", pkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);
#pragma warning restore CS0618
        configuration.ServerConfiguration.DiagnosticsEnabled = false;
        await application.CheckApplicationInstanceCertificatesAsync(true, null, cancellationToken).ConfigureAwait(false);

        var server = new BlackboardServer(endpointUrl, loggers);
        await application.StartAsync(server).ConfigureAwait(false);
        if (loggers is not null)
        {
            Log.Started(loggers.CreateLogger<ServerHost>(), endpointUrl);
        }
        return new ServerHost(application, server);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "OPC UA server started on {EndpointUrl} with the lab profile (a None endpoint, anonymous users): not for production (T-SEC-5)")]
        public static partial void Started(ILogger logger, string endpointUrl);
    }

    /// <summary>Stops the server.</summary>
    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        Server.Dispose();
    }
}
