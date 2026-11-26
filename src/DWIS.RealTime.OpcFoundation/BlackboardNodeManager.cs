using System.Globalization;
using DWIS.RealTime.Core.Adapters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;

namespace DWIS.RealTime.OpcFoundation;

/// <summary>
/// The node manager of the real-time process: the <c>DDHubServer</c> object with its methods and diagnostics, the
/// provider folders with their variables, and the query nodes, all in the shapes <see cref="Naming"/> and
/// <see cref="DDHubMethods"/> give (R-BC-1, R-RT-3). It is the adapter: it implements the three interfaces of
/// decision D-54 and nothing of Core's logic.
/// </summary>
public sealed partial class BlackboardNodeManager : CustomNodeManager2, IAddressSpace, IMethodHost, IQueryNodes
{
    /// <summary>The folder under <c>DDHubServer</c> that holds the query nodes.</summary>
    public const string QueriesFolder = "Queries";

    private readonly ILogger _logger;
    private readonly string _endpointUrl;
    private readonly ushort _namespaceIndex;
    private readonly HashSet<ushort> _claimedIndexes = [];
    private readonly List<string> _claimedUris = [];
    private readonly Dictionary<string, MethodHandler> _handlers = [];
    private readonly Dictionary<string, BaseDataVariableState> _diagnostics = [];
    private readonly Dictionary<string, FolderState> _providerFolders = [];
    private readonly Dictionary<(string Provider, string VariableId), (BaseDataVariableState Variable, BaseDataVariableState Validity)> _variables = [];
    private readonly Dictionary<string, (BaseObjectState Client, BaseDataVariableState Diff)> _queryNodes = [];
    private BaseObjectState? _server;
    private FolderState? _providers;
    private FolderState? _queries;

    /// <summary>Creates the manager; the server calls <see cref="CreateAddressSpace"/> when it starts.</summary>
    public BlackboardNodeManager(IServerInternal server, ApplicationConfiguration configuration, string endpointUrl, ILogger<BlackboardNodeManager>? logger = null)
        : base(server, configuration, Naming.ServerNamespace)
    {
        _endpointUrl = endpointUrl;
        _logger = logger ?? NullLogger<BlackboardNodeManager>.Instance;
        _namespaceIndex = NamespaceIndexes[0];
        _claimedIndexes.Add(_namespaceIndex);
        _claimedUris.Add(Naming.ServerNamespace);
    }

    /// <summary>The namespace index the server gave <see cref="Naming.ServerNamespace"/> (clients look it up by name, D-56).</summary>
    public ushort ServerNamespaceIndex => _namespaceIndex;

    /// <inheritdoc />
    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            _server = new BaseObjectState(null)
            {
                NodeId = new NodeId(DDHubMethods.ObjectName, _namespaceIndex),
                BrowseName = new QualifiedName(DDHubMethods.ObjectName, _namespaceIndex),
                DisplayName = DDHubMethods.ObjectName,
                Description = "The DWIS Blackboard (Rig Edition): inject manifests, resolve and register queries.",
                TypeDefinitionId = ObjectTypeIds.BaseObjectType,
                EventNotifier = EventNotifiers.None,
            };
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            }
            _server.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, _server.NodeId));
            AddPredefinedNode(SystemContext, _server);

            foreach (var (name, description) in DDHubMethods.Diagnostics)
            {
                _diagnostics[name] = CreateDiagnostic(_server, name, description);
            }
            foreach (var declaration in DDHubMethods.All)
            {
                CreateMethod(_server, declaration);
            }
            _providers = CreateFolder(_server, Naming.ProvidersFolder, _namespaceIndex);
            _queries = CreateFolder(_server, QueriesFolder, _namespaceIndex);
        }
        Log.AddressSpaceCreated(_logger, _namespaceIndex, DDHubMethods.All.Count);
    }

    private FolderState CreateFolder(NodeState parent, string name, ushort namespaceIndex)
    {
        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(name, namespaceIndex),
            BrowseName = new QualifiedName(name, namespaceIndex),
            DisplayName = name,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            EventNotifier = EventNotifiers.None,
        };
        parent.AddChild(folder);
        AddPredefinedNode(SystemContext, folder);
        return folder;
    }

    private BaseDataVariableState CreateDiagnostic(BaseObjectState parent, string name, string description)
    {
        var variable = new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(name, _namespaceIndex),
            BrowseName = new QualifiedName(name, _namespaceIndex),
            DisplayName = name,
            Description = description,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.HasProperty,
            DataType = DataTypeIds.Int32,
            ValueRank = ValueRanks.Scalar,
            Value = 0,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    private void CreateMethod(BaseObjectState parent, MethodDeclaration declaration)
    {
        var method = new MethodState(parent)
        {
            NodeId = new NodeId(declaration.Name, _namespaceIndex),
            BrowseName = new QualifiedName(declaration.Name, _namespaceIndex),
            DisplayName = declaration.DisplayName,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            Executable = true,
            UserExecutable = true,
        };
        if (declaration.Inputs.Count > 0)
        {
            method.InputArguments = ArgumentsProperty(method, declaration.Name + "_InputArguments", BrowseNames.InputArguments, declaration.Inputs);
        }
        if (declaration.Outputs.Count > 0)
        {
            method.OutputArguments = ArgumentsProperty(method, declaration.Name + "_OutputArguments", BrowseNames.OutputArguments, declaration.Outputs);
        }
        method.OnCallMethod2 = OnCall;
        parent.AddChild(method);
        AddPredefinedNode(SystemContext, method);
    }

    private PropertyState<Argument[]> ArgumentsProperty(MethodState method, string nodeId, string browseName, IReadOnlyList<MethodArgument> arguments) =>
        new(method)
        {
            NodeId = new NodeId(nodeId, _namespaceIndex),
            BrowseName = new QualifiedName(browseName, 0),
            DisplayName = browseName,
            TypeDefinitionId = VariableTypeIds.PropertyType,
            ReferenceTypeId = ReferenceTypeIds.HasProperty,
            DataType = DataTypeIds.Argument,
            ValueRank = ValueRanks.OneDimension,
            Value = arguments.Select(a => a.Type switch
            {
                ArgumentType.StringArray => new Argument(a.Name, DataTypeIds.String, ValueRanks.OneDimension, a.Description),
                ArgumentType.Boolean => new Argument(a.Name, DataTypeIds.Boolean, ValueRanks.Scalar, a.Description),
                _ => new Argument(a.Name, DataTypeIds.String, ValueRanks.Scalar, a.Description),
            }).ToArray(),
        };

    private ServiceResult OnCall(ISystemContext context, MethodState method, NodeId objectId, IList<object> inputArguments, IList<object> outputArguments)
    {
        var name = method.BrowseName.Name;
        var session = new SessionId((context as ServerSystemContext)?.OperationContext?.SessionId?.ToString() ?? "");
        var input = inputArguments.Count == 0 ? []
            : inputArguments[0] switch
            {
                string[] texts => texts,
                string text => [text],
                _ => Array.Empty<string>(),
            };
        var result = Dispatch(name, session, input);
        if (result.Status != MethodStatus.Good)
        {
            return ToStatusCode(result.Status);
        }
        for (var i = 0; i < outputArguments.Count && i < result.Outputs.Count; i++)
        {
            outputArguments[i] = result.Outputs[i]!;
        }
        return ServiceResult.Good;
    }

    /// <summary>Runs the registered handler of a method as a call from a session would; the adapter contract tests use it.</summary>
    internal MethodResult Dispatch(string name, SessionId session, string[] input)
    {
        MethodHandler? handler;
        lock (Lock)
        {
            _handlers.TryGetValue(name, out handler);
        }
        if (handler is null)
        {
            Log.NoHandler(_logger, name);
            return MethodResult.Fail(MethodStatus.BadNotSupported);
        }
        try
        {
            return handler(session, input);
        }
        catch (Exception ex)
        {
            Log.HandlerFailed(_logger, ex, name);
            return MethodResult.Fail(MethodStatus.BadInternalError);
        }
    }

    private static StatusCode ToStatusCode(MethodStatus status) => status switch
    {
        MethodStatus.Good => StatusCodes.Good,
        MethodStatus.BadNotSupported => StatusCodes.BadNotSupported,
        MethodStatus.BadInvalidArgument => StatusCodes.BadInvalidArgument,
        MethodStatus.BadCommunicationError => StatusCodes.BadCommunicationError,
        _ => StatusCodes.BadInternalError,
    };

    /// <summary>
    /// Registers a namespace URI in the server-wide table and makes this manager the one that answers for it, at
    /// runtime: the per-provider namespaces of 3.1 come with the manifests, after the manager was built. Ported from
    /// the old server (<c>DWISNodeManager.ClaimNamespace</c>), which found that the master node manager's routing
    /// table is taken once, so the explicit registration is needed.
    /// </summary>
    private ushort ClaimNamespace(string uri)
    {
        var index = Server.NamespaceUris.GetIndexOrAppend(uri);
        if (_claimedIndexes.Add(index))
        {
            _claimedUris.Add(uri);
            SetNamespaces([.. _claimedUris]);
            SetNamespaceIndexes([.. _claimedIndexes]);
            Server.NodeManager.RegisterNamespaceManager(uri, this);
        }
        return index;
    }

    // IAddressSpace

    /// <inheritdoc />
    public void CreateProviderFolder(string provider)
    {
        lock (Lock)
        {
            if (_providerFolders.ContainsKey(provider))
            {
                return;
            }
            var index = ClaimNamespace(Naming.ProviderNamespace(provider));
            _providerFolders[provider] = CreateFolder(_providers!, provider, index);
        }
        Log.ProviderCreated(_logger, provider);
    }

    /// <inheritdoc />
    public void DeleteProviderFolder(string provider)
    {
        lock (Lock)
        {
            if (!_providerFolders.Remove(provider, out var folder))
            {
                return;
            }
            foreach (var key in _variables.Keys.Where(k => k.Provider == provider).ToList())
            {
                DeleteVariableCore(key.Provider, key.VariableId);
            }
            RemoveNode(_providers!, folder);
        }
        Log.ProviderDeleted(_logger, provider);
    }

    /// <inheritdoc />
    public NodeAddress CreateVariable(string provider, VariableSpec spec)
    {
        lock (Lock)
        {
            if (!_providerFolders.TryGetValue(provider, out var folder))
            {
                CreateProviderFolder(provider);
                folder = _providerFolders[provider];
            }
            if (!_variables.ContainsKey((provider, spec.VariableId)))
            {
                var index = ClaimNamespace(Naming.VariablesNamespace(provider));
                var variable = NewVariable(folder, index, Naming.VariableNodeId(provider, spec.VariableId), spec.VariableId, DataTypeOf(spec.DataType), spec.Rank, spec.Dimensions, InitialValue(spec));
                var validity = NewVariable(folder, index, Naming.VariableNodeId(provider, spec.ValidityVariableId), spec.ValidityVariableId, DataTypeIds.Boolean, 0, [], false);
                // A client's write is told to Core for the hub role (D-63); Core's own writes go through WriteValue and raise nothing.
                var variableId = spec.VariableId;
                variable.OnWriteValue = (ISystemContext _, NodeState _, NumericRange _, QualifiedName _, ref object value, ref StatusCode _, ref DateTime timestamp) => OnClientWrite(provider, variableId, ref value, ref timestamp);
                _variables[(provider, spec.VariableId)] = (variable, validity);
            }
            return AddressFor(provider, spec.VariableId);
        }
    }

    private BaseDataVariableState NewVariable(NodeState parent, ushort index, string nodeId, string browseName, NodeId dataType, int rank, IReadOnlyList<int> dimensions, object? value)
    {
        var variable = new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(nodeId, index),
            BrowseName = new QualifiedName(browseName, index),
            DisplayName = browseName,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            DataType = dataType,
            ValueRank = rank == 0 ? ValueRanks.Scalar : rank,
            ArrayDimensions = rank > 0 && dimensions.Count > 0 ? new ReadOnlyList<uint>(dimensions.Select(d => (uint)Math.Max(d, 0)).ToList()) : null,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            MinimumSamplingInterval = MinimumSamplingIntervals.Continuous,
            Historizing = false,
            Value = value,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
        };
        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
        return variable;
    }

    /// <inheritdoc />
    public void DeleteVariable(string provider, string variableId)
    {
        lock (Lock)
        {
            DeleteVariableCore(provider, variableId);
        }
    }

    private void DeleteVariableCore(string provider, string variableId)
    {
        if (_variables.Remove((provider, variableId), out var nodes))
        {
            var folder = _providerFolders.GetValueOrDefault(provider);
            RemoveNode(folder, nodes.Validity);
            RemoveNode(folder, nodes.Variable);
        }
    }

    private void RemoveNode(NodeState? parent, BaseInstanceState node)
    {
        parent?.RemoveChild(node);
        DeleteNode(SystemContext, node.NodeId);
    }

    /// <inheritdoc />
    public void WriteValue(string provider, string variableId, object? value, DateTime sourceTimestamp)
    {
        lock (Lock)
        {
            if (_variables.TryGetValue((provider, variableId), out var nodes))
            {
                nodes.Variable.Value = value;
                nodes.Variable.Timestamp = sourceTimestamp;
                nodes.Variable.StatusCode = StatusCodes.Good;
                nodes.Variable.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    /// <inheritdoc />
    public TimedValue? ReadValue(string provider, string variableId)
    {
        lock (Lock)
        {
            return _variables.TryGetValue((provider, variableId), out var nodes) ? new TimedValue(nodes.Variable.Value, nodes.Variable.Timestamp) : null;
        }
    }

    /// <inheritdoc />
    public NodeAddress AddressFor(string provider, string variableId)
    {
        var uri = Naming.VariablesNamespace(provider);
        return new NodeAddress(_endpointUrl, uri, Server.NamespaceUris.GetIndex(uri) is var index && index >= 0 ? (ushort)index : (ushort)0, Naming.VariableNodeId(provider, variableId));
    }

    /// <summary>The OPC UA data type of one of the seven manifest type names; string for anything else.</summary>
    public static NodeId DataTypeOf(string dataType) => dataType.ToUpperInvariant() switch
    {
        "DOUBLE" => DataTypeIds.Double,
        "FLOAT" => DataTypeIds.Float,
        "INT" => DataTypeIds.Int32,
        "LONG" => DataTypeIds.Int64,
        "SHORT" => DataTypeIds.Int16,
        "BOOL" => DataTypeIds.Boolean,
        _ => DataTypeIds.String,
    };

    /// <summary>The initial value of a spec: the text parsed as the type, or the type's default; arrays start empty (null).</summary>
    public static object? InitialValue(VariableSpec spec)
    {
        if (spec.Rank > 0)
        {
            return null;
        }
        var text = spec.InitialValue;
        var hasText = !string.IsNullOrEmpty(text);
        try
        {
            return spec.DataType.ToUpperInvariant() switch
            {
                "DOUBLE" => hasText ? double.Parse(text, CultureInfo.InvariantCulture) : 0.0,
                "FLOAT" => hasText ? float.Parse(text, CultureInfo.InvariantCulture) : 0.0f,
                "INT" => hasText ? int.Parse(text, CultureInfo.InvariantCulture) : 0,
                "LONG" => hasText ? long.Parse(text, CultureInfo.InvariantCulture) : 0L,
                "SHORT" => hasText ? short.Parse(text, CultureInfo.InvariantCulture) : (short)0,
                "BOOL" => hasText && bool.Parse(text),
                _ => text,
            };
        }
        catch (FormatException)
        {
            return DataTypeOf(spec.DataType) == DataTypeIds.String ? text : null;
        }
    }

    // IMethodHost

    /// <inheritdoc />
    public void RegisterMethod(string name, MethodHandler handler)
    {
        if (DDHubMethods.Find(name) is null)
        {
            throw new ArgumentException($"{name} is not a DDHubServer method.", nameof(name));
        }
        lock (Lock)
        {
            _handlers[name] = handler;
        }
    }

    /// <inheritdoc />
    public void SetDiagnostic(string name, int value)
    {
        lock (Lock)
        {
            if (_diagnostics.TryGetValue(name, out var variable))
            {
                variable.Value = value;
                variable.Timestamp = DateTime.UtcNow;
                variable.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    // IQueryNodes

    /// <inheritdoc />
    public NodeAddress CreateQueryNodes(int queryId, SessionId session)
    {
        var clientId = Naming.QueryClientNodeId(queryId, session);
        var diffId = Naming.QueryDiffNodeId(queryId, session);
        lock (Lock)
        {
            if (!_queryNodes.ContainsKey(clientId))
            {
                var client = new BaseObjectState(_queries)
                {
                    NodeId = new NodeId(clientId, _namespaceIndex),
                    BrowseName = new QualifiedName(clientId, _namespaceIndex),
                    DisplayName = clientId,
                    TypeDefinitionId = ObjectTypeIds.BaseObjectType,
                    ReferenceTypeId = ReferenceTypeIds.Organizes,
                    EventNotifier = EventNotifiers.None,
                };
                _queries!.AddChild(client);
                AddPredefinedNode(SystemContext, client);
                var diff = new BaseDataVariableState(client)
                {
                    NodeId = new NodeId(diffId, _namespaceIndex),
                    BrowseName = new QualifiedName(diffId, _namespaceIndex),
                    DisplayName = diffId,
                    TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                    ReferenceTypeId = ReferenceTypeIds.HasComponent,
                    DataType = DataTypeIds.String,
                    ValueRank = ValueRanks.Scalar,
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    MinimumSamplingInterval = MinimumSamplingIntervals.Continuous,
                    Value = "",
                    StatusCode = StatusCodes.Good,
                    Timestamp = DateTime.UtcNow,
                };
                client.AddChild(diff);
                AddPredefinedNode(SystemContext, diff);
                _queryNodes[clientId] = (client, diff);
            }
        }
        return new NodeAddress(_endpointUrl, Naming.ServerNamespace, _namespaceIndex, diffId);
    }

    /// <inheritdoc />
    public void DeleteQueryNodes(int queryId, SessionId session)
    {
        lock (Lock)
        {
            if (_queryNodes.Remove(Naming.QueryClientNodeId(queryId, session), out var nodes))
            {
                RemoveNode(nodes.Client, nodes.Diff);
                RemoveNode(_queries, nodes.Client);
            }
        }
    }

    /// <inheritdoc />
    public void WriteDiff(int queryId, SessionId session, string json)
    {
        lock (Lock)
        {
            if (_queryNodes.TryGetValue(Naming.QueryClientNodeId(queryId, session), out var nodes))
            {
                nodes.Diff.Value = json;
                nodes.Diff.Timestamp = DateTime.UtcNow;
                nodes.Diff.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "DDHubServer created at namespace index {Index} with {MethodCount} methods")]
        public static partial void AddressSpaceCreated(ILogger logger, ushort index, int methodCount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DDHubServer.{Method} called before a handler was registered")]
        public static partial void NoHandler(ILogger logger, string method);

        [LoggerMessage(Level = LogLevel.Error, Message = "DDHubServer.{Method} failed")]
        public static partial void HandlerFailed(ILogger logger, Exception exception, string method);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Provider folder {Provider} created")]
        public static partial void ProviderCreated(ILogger logger, string provider);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Provider folder {Provider} deleted")]
        public static partial void ProviderDeleted(ILogger logger, string provider);
    }
}
