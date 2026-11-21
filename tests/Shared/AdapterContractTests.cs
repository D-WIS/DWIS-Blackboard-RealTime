using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Tests.Contract;

/// <summary>
/// The contract of the three adapter interfaces (decision D-54, T-MOD-5): the same tests run against the test double
/// (Core.Tests) and against the OPC Foundation adapter on an in-process server (OpcFoundation.Tests). A company
/// writing an adapter for another stack runs them against its own.
/// </summary>
public abstract class AdapterContractTests
{
    /// <summary>The adapter under test; one per test method.</summary>
    protected abstract (IAddressSpace AddressSpace, IMethodHost Methods, IQueryNodes QueryNodes) Adapter { get; }

    /// <summary>Calls a registered method the way the stack would when a client calls it.</summary>
    protected abstract MethodResult Invoke(string name, SessionId session, params string[] input);

    private static VariableSpec Spec(string id, string type, string initial = "") => new(id, type, 0, [], initial, id + Naming.ValiditySuffix);

    [Fact]
    public void A_provider_folder_and_a_variable_have_the_names_clients_expect()
    {
        var (space, _, _) = Adapter;
        space.CreateProviderFolder("Acme");
        space.CreateProviderFolder("Acme"); // twice is harmless

        var address = space.CreateVariable("Acme", Spec("SPP", "double", "1.5"));

        address.NamespaceUri.Should().Be("http://ddhub.no/Acme/Variables/");
        address.NodeId.Should().Be("Acme.SPP");
        address.EndpointUrl.Should().StartWith("opc.tcp://");
        address.NamespaceIndex.Should().BeGreaterThan(0, "the namespace is in the server's table");
        space.AddressFor("Acme", "SPP").Should().Be(address);
        space.ReadValue("Acme", "SPP").Should().NotBeNull().And.Subject.As<TimedValue>().Value.Should().Be(1.5);
    }

    [Theory]
    [InlineData("double", 0.0)]
    [InlineData("float", 0.0f)]
    [InlineData("int", 0)]
    [InlineData("long", 0L)]
    [InlineData("short", (short)0)]
    [InlineData("string", "")]
    [InlineData("bool", false)]
    public void Every_manifest_data_type_creates_a_variable_with_the_type_default(string dataType, object expected)
    {
        var (space, _, _) = Adapter;
        space.CreateProviderFolder("Types");

        space.CreateVariable("Types", Spec("v_" + dataType, dataType));

        var read = space.ReadValue("Types", "v_" + dataType);
        read.Should().NotBeNull();
        read!.Value.Should().Be(expected);
    }

    [Fact]
    public void A_write_is_read_back_with_its_source_timestamp()
    {
        var (space, _, _) = Adapter;
        space.CreateProviderFolder("Acme");
        space.CreateVariable("Acme", Spec("WOB", "double"));
        var at = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        space.WriteValue("Acme", "WOB", 42.0, at);

        var read = space.ReadValue("Acme", "WOB");
        read.Should().NotBeNull();
        read!.Value.Should().Be(42.0);
        read.SourceTimestamp.Should().Be(at);
    }

    [Fact]
    public void Deleting_a_variable_or_a_provider_removes_what_was_created_and_is_harmless_twice()
    {
        var (space, _, _) = Adapter;
        space.CreateProviderFolder("Acme");
        space.CreateVariable("Acme", Spec("A", "int"));
        space.CreateVariable("Acme", Spec("B", "int"));

        space.DeleteVariable("Acme", "A");
        space.DeleteVariable("Acme", "A");
        space.ReadValue("Acme", "A").Should().BeNull();
        space.ReadValue("Acme", "B").Should().NotBeNull();

        space.DeleteProviderFolder("Acme");
        space.DeleteProviderFolder("Acme");
        space.ReadValue("Acme", "B").Should().BeNull();
        space.DeleteProviderFolder("NeverCreated");
    }

    [Fact]
    public void A_registered_method_answers_with_the_handlers_status_and_outputs()
    {
        var (_, methods, _) = Adapter;
        var session = new SessionId("s1");
        SessionId? seen = null;
        string[]? input = null;
        methods.RegisterMethod(DDHubMethods.ResolveQuery, (s, i) => { seen = s; input = i; return MethodResult.Good("{\"VariablesHeader\":[]}"); });
        methods.RegisterMethod(DDHubMethods.Register, (_, _) => MethodResult.Fail(MethodStatus.BadNotSupported));

        var good = Invoke(DDHubMethods.ResolveQuery, session, "SELECT * WHERE { ?s ?p ?o }");
        var bad = Invoke(DDHubMethods.Register, session, "{}");

        good.Status.Should().Be(MethodStatus.Good);
        good.Outputs.Should().ContainSingle().Which.Should().Be("{\"VariablesHeader\":[]}");
        seen.Should().Be(session);
        input.Should().Equal("SELECT * WHERE { ?s ?p ?o }");
        bad.Status.Should().Be(MethodStatus.BadNotSupported);
        methods.SetDiagnostic(DDHubMethods.RegisteredQueryCount, 3);
    }

    [Fact]
    public void Query_nodes_are_named_as_the_old_server_names_them_and_take_a_diff()
    {
        var (_, _, nodes) = Adapter;
        var session = new SessionId("ns=1;i=42");

        var address = nodes.CreateQueryNodes(7, session);
        nodes.CreateQueryNodes(7, session); // twice is harmless

        address.NamespaceUri.Should().Be(Naming.DictionariesNamespace);
        address.NodeId.Should().Be(Naming.QueryDiffNodeId(7, session)).And.StartWith("QueryDiff7_");
        nodes.WriteDiff(7, session, "{\"QueryID\":\"QueryClient7\",\"Added\":[],\"Removed\":[]}");
        nodes.DeleteQueryNodes(7, session);
        nodes.DeleteQueryNodes(7, session);
        nodes.DeleteQueryNodes(8, session);
    }
}
