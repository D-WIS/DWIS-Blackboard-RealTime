using DWIS.RealTime.Core;
using DWIS.RealTime.Core.Adapters;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace DWIS.RealTime.OpcFoundation.Tests;

/// <summary>
/// What a client sees (the "done when" of M4.0): the <c>DDHubServer</c> object found through the namespace array
/// (decision D-56), its nine methods and two diagnostics, a call that answers a status rather than a fault, the
/// session events reaching Core, and a provided variable readable and writable over the wire (R-RT-1).
/// </summary>
public sealed class ClientRoundTripTests : IClassFixture<ServerFixture>, IAsyncLifetime
{
    private readonly ServerFixture _server;
    private Session _session = null!;
    private RealTimeCore _core = null!;

    public ClientRoundTripTests(ServerFixture server)
    {
        _server = server;
    }

    public async ValueTask InitializeAsync()
    {
        var adapter = _server.NodeManager;
        _core = new RealTimeCore(adapter, adapter, adapter, options: new RealTimeOptions { EndpointUrl = _server.EndpointUrl, RetrySeconds = 1 });
        _server.Host.Server.SessionEvents = _core;
        await _core.StartAsync(TestContext.Current.CancellationToken);

        var application = new ApplicationInstance { ApplicationName = "DWIS Blackboard test client", ApplicationType = ApplicationType.Client };
        var configuration = await application.Build("urn:d-wis:blackboard:realtime:tests", "https://github.com/D-WIS/DWIS-Blackboard-RealTime")
            .AsClient()
            .AddSecurityConfiguration("CN=DWIS Blackboard test client", Path.Combine(Path.GetTempPath(), "dwis-realtime-tests", "client-pki"))
            .SetAutoAcceptUntrustedCertificates(true)
            .Create();
        await application.CheckApplicationInstanceCertificatesAsync(true, null, CancellationToken.None);
        var endpoint = CoreClientUtils.SelectEndpoint(configuration, _server.EndpointUrl, useSecurity: false);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));
        _session = await Session.Create(configuration, configured, updateBeforeConnect: false, "round trip", 60_000, new UserIdentity(new AnonymousIdentityToken()), null, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.CloseAsync();
            _session.Dispose();
        }
        if (_core is not null)
        {
            await _core.DisposeAsync();
        }
    }

    private static readonly object[] EmptyManifest = [new[] { "{}" }];

    private ushort Dictionaries => (ushort)_session.NamespaceUris.GetIndex(Naming.DictionariesNamespace);

    [Fact]
    public void The_DDHubServer_object_is_found_by_namespace_name_with_its_methods_and_diagnostics()
    {
        _session.NamespaceUris.GetIndex(Naming.DictionariesNamespace).Should().BeGreaterThan(0, "the namespace is in the server's table");
        var serverNode = new NodeId(DDHubMethods.ObjectName, Dictionaries);

        _session.Browse(null, null, serverNode, 0, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, 0, out _, out var children);

        children.Where(c => c.NodeClass == NodeClass.Method).Select(c => c.BrowseName.Name).Should().BeEquivalentTo(DDHubMethods.All.Select(m => m.Name));
        children.Where(c => c.NodeClass == NodeClass.Variable).Select(c => c.BrowseName.Name).Should().BeEquivalentTo(DDHubMethods.RegisteredQueryCount, DDHubMethods.RegisteredQuerySubscriptionCount);
        children.Where(c => c.NodeClass == NodeClass.Object).Select(c => c.BrowseName.Name).Should().BeEquivalentTo(Naming.ProvidersFolder, BlackboardNodeManager.QueriesFolder);
        _session.ReadValue(new NodeId(DDHubMethods.RegisteredQueryCount, Dictionaries)).Value.Should().Be(0);
    }

    [Fact]
    public void A_method_call_answers_a_status_not_a_fault()
    {
        var objectNode = new NodeId(DDHubMethods.ObjectName, Dictionaries);

        var call = () => _session.Call(objectNode, new NodeId(DDHubMethods.Register, Dictionaries), EmptyManifest);

        call.Should().Throw<ServiceResultException>().Which.StatusCode.Should().Be(StatusCodes.BadNotSupported);
    }

    [Fact]
    public void The_input_arguments_are_declared_as_the_old_server_declares_them()
    {
        var inputs = _session.ReadValue(new NodeId(DDHubMethods.Inject + "_InputArguments", Dictionaries)).Value as ExtensionObject[];

        inputs.Should().NotBeNull().And.HaveCount(1);
        var argument = inputs![0].Body.Should().BeOfType<Argument>().Subject;
        argument.Name.Should().Be("ManifestFile");
        argument.DataType.Should().Be(DataTypeIds.String);
        argument.ValueRank.Should().Be(ValueRanks.OneDimension);
    }

    [Fact]
    public void The_session_is_known_to_core_while_it_is_open()
    {
        _core.OpenSessions.Should().Contain(s => s.Value == _session.SessionId.ToString());
    }

    [Fact]
    public async Task A_provided_variable_is_read_and_written_over_the_wire()
    {
        var adapter = _server.NodeManager;
        adapter.CreateProviderFolder("Wire");
        var address = adapter.CreateVariable("Wire", new VariableSpec("Flow", "double", 0, [], "3.5", "Flow_validitySignal"));
        await _session.FetchNamespaceTablesAsync(TestContext.Current.CancellationToken); // the provider namespace was claimed after the session opened
        var index = (ushort)_session.NamespaceUris.GetIndex(address.NamespaceUri);
        index.Should().Be(address.NamespaceIndex, "the adapter reports the index the client sees");
        var nodeId = new NodeId(address.NodeId, index);

        _session.ReadValue(nodeId).Value.Should().Be(3.5);
        var write = new WriteValueCollection { new WriteValue { NodeId = nodeId, AttributeId = Attributes.Value, Value = new DataValue(new Variant(7.25)) } };
        var response = await _session.WriteAsync(null, write, CancellationToken.None);
        response.Results.Should().ContainSingle().Which.Should().Be(StatusCodes.Good);

        adapter.ReadValue("Wire", "Flow")!.Value.Should().Be(7.25);
        _session.ReadValue(new NodeId("Wire.Flow_validitySignal", index)).Value.Should().Be(false);
        index.Should().BeGreaterThan(Dictionaries, "the provider namespace was claimed at runtime");
    }
}
