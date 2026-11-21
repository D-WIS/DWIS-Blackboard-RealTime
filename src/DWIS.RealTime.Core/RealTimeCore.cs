using DWIS.RealTime.Core.Adapters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DWIS.RealTime.Core;

/// <summary>
/// The façade logic of the real-time process (specification 3.1): registers the nine <c>DDHubServer</c> method handlers
/// with the adapter, keeps the diagnostics, and hears about sessions. Milestone 4.0: every handler answers
/// <see cref="MethodStatus.BadNotSupported"/> with the right shape; M4.1 fills the injection side, M4.2 the query side.
/// </summary>
public sealed partial class RealTimeCore : ISessionEvents
{
    private readonly IAddressSpace _addressSpace;
    private readonly IMethodHost _methods;
    private readonly IQueryNodes _queryNodes;
    private readonly RealTimeOptions _options;
    private readonly ILogger _logger;
    private readonly HashSet<SessionId> _sessions = [];
    private readonly Lock _gate = new();

    /// <summary>Creates the core over one adapter (its three interfaces may be one object, as the OPC Foundation adapter is).</summary>
    public RealTimeCore(IAddressSpace addressSpace, IMethodHost methods, IQueryNodes queryNodes, RealTimeOptions? options = null, ILogger<RealTimeCore>? logger = null)
    {
        _addressSpace = addressSpace;
        _methods = methods;
        _queryNodes = queryNodes;
        _options = options ?? new RealTimeOptions();
        _logger = logger ?? NullLogger<RealTimeCore>.Instance;
    }

    /// <summary>The settings in force.</summary>
    public RealTimeOptions Options => _options;

    /// <summary>The sessions open right now, as the adapter reported them.</summary>
    public IReadOnlyCollection<SessionId> OpenSessions
    {
        get { lock (_gate) { return [.. _sessions]; } }
    }

    /// <summary>
    /// Registers the nine method handlers and sets the diagnostics to zero. Call once the server is up (the adapter's
    /// nodes exist). The rebuild from the semantic service (R-RT-6, D-52) comes with M4.1.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var declaration in DDHubMethods.All)
        {
            _methods.RegisterMethod(declaration.Name, (session, input) => Handle(declaration, session, input));
        }
        _methods.SetDiagnostic(DDHubMethods.RegisteredQueryCount, 0);
        _methods.SetDiagnostic(DDHubMethods.RegisteredQuerySubscriptionCount, 0);
        Log.Started(_logger, DDHubMethods.All.Count, _options.SemanticAddress, _options.EndpointUrl);
        _ = _addressSpace;
        _ = _queryNodes;
        return Task.CompletedTask;
    }

    /// <summary>Stops the core: nothing to release in M4.0 (M4.2 ends the watch stream).</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log.Stopped(_logger);
        return Task.CompletedTask;
    }

    private MethodResult Handle(MethodDeclaration declaration, SessionId session, string[] input)
    {
        Log.MethodCalled(_logger, declaration.Name, session.Value, input.Length);
        if (declaration.Inputs.Count > 0 && (input.Length != 1 || input[0] is null))
        {
            return MethodResult.Fail(MethodStatus.BadInvalidArgument);
        }
        // M4.0: the façade has its shape and no behaviour yet. Register and Resolve stay this way (R-BC-2).
        return MethodResult.Fail(MethodStatus.BadNotSupported);
    }

    /// <inheritdoc />
    public void SessionOpened(SessionId session)
    {
        lock (_gate) { _sessions.Add(session); }
        Log.SessionOpened(_logger, session.Value);
    }

    /// <inheritdoc />
    public void SessionClosed(SessionId session)
    {
        bool known;
        lock (_gate) { known = _sessions.Remove(session); }
        Log.SessionClosed(_logger, session.Value, known);
        // M4.2: release the session's registered queries (ReleaseOwner) and delete its query nodes.
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Real-time core started: {MethodCount} DDHubServer methods registered; semantic service {SemanticAddress}; endpoint {EndpointUrl}")]
        public static partial void Started(ILogger logger, int methodCount, string semanticAddress, string endpointUrl);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DDHubServer.{Method} called by session {Session} with {InputCount} input(s)")]
        public static partial void MethodCalled(ILogger logger, string method, string session, int inputCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "Real-time core stopped")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {Session} opened")]
        public static partial void SessionOpened(ILogger logger, string session);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {Session} closed (known: {Known})")]
        public static partial void SessionClosed(ILogger logger, string session, bool known);
    }
}
