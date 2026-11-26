using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Hub;
using DWIS.RealTime.Core.Injection;
using DWIS.RealTime.Core.Queries;
using DWIS.RealTime.Core.Semantic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DWIS.RealTime.Core;

/// <summary>
/// The façade logic of the real-time process (specification 3.1): registers the nine <c>DDHubServer</c> method
/// handlers with the adapter, runs the injection side (M4.1) and the query side (M4.2) over the semantic service,
/// rebuilds the address space from the service at start-up and once more after it (R-RT-6, D-52), keeps one watch
/// stream open for the query diffs (D-53), keeps the diagnostics, and hears about sessions.
/// </summary>
public sealed partial class RealTimeCore : ISessionEvents, IValueEvents, IAsyncDisposable
{
    private readonly IAddressSpace _addressSpace;
    private readonly IMethodHost _methods;
    private readonly ISemanticService _semantic;
    private readonly RealTimeOptions _options;
    private readonly ILogger _logger;
    private readonly HashSet<SessionId> _sessions = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _rebuild;
    private Task? _watch;

    /// <summary>
    /// Creates the core over one adapter (its three interfaces may be one object, as the OPC Foundation adapter is) and the
    /// semantic service. The hub role runs when the options name a hub and the adapter's codec and a hub connection are given (D-63).
    /// </summary>
    public RealTimeCore(IAddressSpace addressSpace, IMethodHost methods, IQueryNodes queryNodes, ISemanticService? semantic = null, RealTimeOptions? options = null, ILoggerFactory? loggers = null, IReplicationCodec? codec = null, IHubConnection? hub = null)
    {
        _addressSpace = addressSpace;
        _methods = methods;
        _semantic = semantic ?? new UnavailableSemanticService();
        _options = options ?? new RealTimeOptions();
        _logger = loggers?.CreateLogger<RealTimeCore>() ?? NullLogger<RealTimeCore>.Instance;
        Live = new LiveManifests();
        Injector = new Injector(_addressSpace, _semantic, Live, loggers?.CreateLogger<Injector>());
        Queries = new QueryFacade(queryNodes, methods, _semantic, loggers?.CreateLogger<QueryFacade>());
        if (_options.Hub.Enabled && codec is not null && hub is not null)
        {
            Hub = new HubReplicator(hub, codec, _addressSpace, Injector, _options.Hub, loggers?.CreateLogger<HubReplicator>());
        }
    }

    /// <summary>The settings in force.</summary>
    public RealTimeOptions Options => _options;

    /// <summary>The manifests live in the service with the nodes this process created for them.</summary>
    public LiveManifests Live { get; }

    /// <summary>The injection side of the façade.</summary>
    public Injector Injector { get; }

    /// <summary>The query side of the façade.</summary>
    public QueryFacade Queries { get; }

    /// <summary>The hub role, or null when no hub is configured (specification section 6).</summary>
    public HubReplicator? Hub { get; }

    /// <summary>True once the first rebuild from the service succeeded (the address space reflects the service).</summary>
    public bool Rebuilt { get; private set; }

    /// <summary>The sessions open right now, as the adapter reported them.</summary>
    public IReadOnlyCollection<SessionId> OpenSessions
    {
        get { lock (_gate) { return [.. _sessions]; } }
    }

    /// <summary>
    /// Registers the nine method handlers, sets the diagnostics to zero, starts the rebuild (the list of the service's
    /// manifests, taken again every <see cref="RealTimeOptions.RetrySeconds"/> until the service answers, then once
    /// more: D-52 rule 2) and the watch stream. Call once the server is up.
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
        var retry = TimeSpan.FromSeconds(Math.Max(1, _options.RetrySeconds));
        _rebuild = RebuildLoopAsync(retry, _stopping.Token);
        _watch = Queries.WatchLoopAsync(retry, _stopping.Token);
        Hub?.Start(retry);
        return Task.CompletedTask;
    }

    /// <summary>Waits for the rebuild to complete (tests and the host's readiness).</summary>
    public Task WhenRebuiltAsync() => _rebuild ?? Task.CompletedTask;

    private async Task RebuildLoopAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
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

    /// <summary>Stops the core: ends the rebuild loop and the watch stream.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { _rebuild, _watch })
        {
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        Log.Stopped(_logger);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Hub is not null)
        {
            await Hub.DisposeAsync().ConfigureAwait(false);
        }
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
        // The stack calls the handler on its own thread and waits; a call is one or two round trips to the service,
        // the old server's behaviour too, and the variable paths are not involved (R-RT-1).
        var token = _stopping.Token;
        switch (declaration.Name)
        {
            case DDHubMethods.Inject:
            case DDHubMethods.Update:
            {
                var outcome = Injector.InjectAsync(input[0], token).GetAwaiter().GetResult();
                return outcome.Status == MethodStatus.Good ? MethodResult.Good(outcome.ResultJson) : MethodResult.Fail(outcome.Status);
            }
            case DDHubMethods.Delete:
            {
                var outcome = Injector.DeleteAsync(input[0], token).GetAwaiter().GetResult();
                return outcome.Status == MethodStatus.Good ? MethodResult.Good(outcome.Removed) : MethodResult.Fail(outcome.Status);
            }
            case DDHubMethods.ResolveQuery:
            {
                var (status, json) = Queries.ResolveAsync(input[0], token).GetAwaiter().GetResult();
                return status == MethodStatus.Good ? MethodResult.Good(json) : MethodResult.Fail(status);
            }
            case DDHubMethods.GetResources:
            {
                var (status, json) = Queries.GetResourcesAsync(token).GetAwaiter().GetResult();
                return status == MethodStatus.Good ? MethodResult.Good(json) : MethodResult.Fail(status);
            }
            case DDHubMethods.RegisterQuery:
            {
                if (string.IsNullOrEmpty(session.Value))
                {
                    return MethodResult.Fail(MethodStatus.BadInvalidArgument);
                }
                var (status, diff, nodeId) = Queries.RegisterAsync(input[0], session, token).GetAwaiter().GetResult();
                return status == MethodStatus.Good ? MethodResult.Good(diff, nodeId) : MethodResult.Fail(status);
            }
            case DDHubMethods.UnregisterQuery:
            {
                var (status, released) = Queries.UnregisterAsync(input[0], session, token).GetAwaiter().GetResult();
                return status == MethodStatus.Good ? MethodResult.Good(released) : MethodResult.Fail(status);
            }
            default:
                // Register and Resolve: acquisition files are gone (R-BC-2).
                return MethodResult.Fail(MethodStatus.BadNotSupported);
        }
    }

    /// <inheritdoc />
    public void ValueWritten(string provider, string variableId, object? value, DateTime sourceTimestamp) => Hub?.ValueWritten(provider, variableId, value, sourceTimestamp);

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
        // The stack calls this on its own thread while closing the session; the release goes on without it.
        _ = Queries.SessionClosedAsync(session, _stopping.Token);
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
