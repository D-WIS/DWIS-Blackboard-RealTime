using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Injection;
using DWIS.RealTime.Core.Semantic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DWIS.RealTime.Core;

/// <summary>
/// The façade logic of the real-time process (specification 3.1): registers the nine <c>DDHubServer</c> method
/// handlers with the adapter, runs the injection side over the semantic service (M4.1), rebuilds the address space
/// from the service at start-up and once more after it (R-RT-6, D-52), keeps the diagnostics, and hears about
/// sessions. The query side comes with M4.2.
/// </summary>
public sealed partial class RealTimeCore : ISessionEvents, IAsyncDisposable
{
    private readonly IAddressSpace _addressSpace;
    private readonly IMethodHost _methods;
    private readonly IQueryNodes _queryNodes;
    private readonly ISemanticService _semantic;
    private readonly RealTimeOptions _options;
    private readonly ILogger _logger;
    private readonly HashSet<SessionId> _sessions = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _rebuild;

    /// <summary>Creates the core over one adapter (its three interfaces may be one object, as the OPC Foundation adapter is) and the semantic service.</summary>
    public RealTimeCore(IAddressSpace addressSpace, IMethodHost methods, IQueryNodes queryNodes, ISemanticService? semantic = null, RealTimeOptions? options = null, ILoggerFactory? loggers = null)
    {
        _addressSpace = addressSpace;
        _methods = methods;
        _queryNodes = queryNodes;
        _semantic = semantic ?? new UnavailableSemanticService();
        _options = options ?? new RealTimeOptions();
        _logger = loggers?.CreateLogger<RealTimeCore>() ?? NullLogger<RealTimeCore>.Instance;
        Live = new LiveManifests();
        Injector = new Injector(_addressSpace, _semantic, Live, loggers?.CreateLogger<Injector>());
    }

    /// <summary>The settings in force.</summary>
    public RealTimeOptions Options => _options;

    /// <summary>The manifests live in the service with the nodes this process created for them.</summary>
    public LiveManifests Live { get; }

    /// <summary>The injection side of the façade.</summary>
    public Injector Injector { get; }

    /// <summary>True once the first rebuild from the service succeeded (the address space reflects the service).</summary>
    public bool Rebuilt { get; private set; }

    /// <summary>The sessions open right now, as the adapter reported them.</summary>
    public IReadOnlyCollection<SessionId> OpenSessions
    {
        get { lock (_gate) { return [.. _sessions]; } }
    }

    /// <summary>
    /// Registers the nine method handlers, sets the diagnostics to zero, and starts the rebuild: the list of the
    /// service's manifests, taken again every <see cref="RealTimeOptions.RetrySeconds"/> until the service answers,
    /// then once more (D-52 rule 2: a commit that landed while the process was down). Call once the server is up.
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
        _rebuild = RebuildLoopAsync(_stopping.Token);
        _ = _queryNodes;
        return Task.CompletedTask;
    }

    /// <summary>Waits for the rebuild to complete (tests and the host's readiness).</summary>
    public Task WhenRebuiltAsync() => _rebuild ?? Task.CompletedTask;

    private async Task RebuildLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.RetrySeconds));
        var listed = 0;
        while (listed < 2 && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var count = await Injector.RebuildAsync(cancellationToken).ConfigureAwait(false);
                listed++;
                Rebuilt = true;
                Log.Rebuilt(_logger, listed, count);
            }
            catch (SemanticException e)
            {
                Log.RebuildWaiting(_logger, e.Message, delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Stops the core: ends the rebuild loop (M4.2 ends the watch stream).</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_rebuild is not null)
        {
            try { await _rebuild.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        Log.Stopped(_logger);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Injector.Dispose();
        _stopping.Dispose();
    }

    private MethodResult Handle(MethodDeclaration declaration, SessionId session, string[] input)
    {
        Log.MethodCalled(_logger, declaration.Name, session.Value, input.Length);
        if (declaration.Inputs.Count > 0 && (input.Length != 1 || input[0] is null))
        {
            return MethodResult.Fail(MethodStatus.BadInvalidArgument);
        }
        switch (declaration.Name)
        {
            case DDHubMethods.Inject:
            case DDHubMethods.Update:
            {
                // The stack calls the handler on its own thread and waits; the injection is one call to the service after
                // another, so blocking here is the old server's behaviour too, and the variable paths are not involved (R-RT-1).
                var outcome = Injector.InjectAsync(input[0], _stopping.Token).GetAwaiter().GetResult();
                return outcome.Status == MethodStatus.Good ? MethodResult.Good(outcome.ResultJson) : MethodResult.Fail(outcome.Status);
            }
            case DDHubMethods.Delete:
            {
                var outcome = Injector.DeleteAsync(input[0], _stopping.Token).GetAwaiter().GetResult();
                return outcome.Status == MethodStatus.Good ? MethodResult.Good(outcome.Removed) : MethodResult.Fail(outcome.Status);
            }
            default:
                // Register and Resolve stay this way (R-BC-2); the query methods come with M4.2.
                return MethodResult.Fail(MethodStatus.BadNotSupported);
        }
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Real-time core stopped")]
        public static partial void Stopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Rebuild {Round} from the semantic service: {ManifestCount} manifests live")]
        public static partial void Rebuilt(ILogger logger, int round, int manifestCount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The semantic service did not answer the list of manifests ({Reason}); trying again in {Seconds} s; values keep flowing meanwhile (R-RT-2)")]
        public static partial void RebuildWaiting(ILogger logger, string reason, double seconds);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DDHubServer.{Method} called by session {Session} with {InputCount} input(s)")]
        public static partial void MethodCalled(ILogger logger, string method, string session, int inputCount);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {Session} opened")]
        public static partial void SessionOpened(ILogger logger, string session);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {Session} closed (known: {Known})")]
        public static partial void SessionClosed(ILogger logger, string session, bool known);
    }
}
