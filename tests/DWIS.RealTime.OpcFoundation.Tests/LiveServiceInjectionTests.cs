using System.Text.Json;
using DWIS.RealTime.Core;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Semantic;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace DWIS.RealTime.OpcFoundation.Tests;

/// <summary>
/// The whole path over the wire against a running semantic service: a client calls <c>Inject</c> on the OPC UA
/// server, the manifest goes through prepare and commit, the variables exist, <c>Delete</c> takes them away.
/// Runs only when <c>DWIS_SEMANTIC_ADDRESS</c> names the gRPC address of a service (the compose stack of the Tests
/// repository: <c>http://127.0.0.1:8081</c>); skipped otherwise, since the unit tests of this repository need no stack.
/// </summary>
public sealed class LiveServiceInjectionTests : IClassFixture<ServerFixture>
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("DWIS_SEMANTIC_ADDRESS");
    private readonly ServerFixture _server;

    public LiveServiceInjectionTests(ServerFixture server)
    {
        _server = server;
    }

    private const string ManifestJson = """
        {
          "ManifestName": "Round trip signals",
          "Provider": { "Name": "RoundTrip", "Company": "D-WIS" },
          "InjectionInformation": { "InjectedNodesNamespaceAlias": "nodes", "ProvidedVariablesNamespaceAlias": "pv", "InjectedVariablesNamespaceAlias": "variables" },
          "ProvidedVariables": [ { "VariableID": "SPP", "DataType": "double", "Rank": 0, "InitialValue": "12.5" } ],
          "InjectedNodes": [ { "UniqueName": "SPPPoint", "BrowseName": "SPPPoint", "DisplayName": "SPPPoint", "TypeDictionaryURI": "DrillingDataPoint" } ],
          "InjectedReferences": [ { "Subject": { "NameSpace": "nodes", "ID": "SPPPoint" }, "VerbURI": "http://ddhub.no/HasDynamicValue", "Object": { "NameSpace": "pv", "ID": "SPP" } } ]
        }
        """;

    [Fact]
    public async Task Inject_and_delete_over_the_wire_against_the_service()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Address), "DWIS_SEMANTIC_ADDRESS is not set: no semantic service to talk to.");
        var adapter = _server.NodeManager;
        using var semantic = new GrpcSemanticService(Address!, TimeSpan.FromSeconds(60));
        await using var core = new RealTimeCore(adapter, adapter, adapter, semantic, new RealTimeOptions { EndpointUrl = _server.EndpointUrl, SemanticAddress = Address!, RetrySeconds = 1 });
        _server.Host.Server.SessionEvents = core;
        await core.StartAsync(TestContext.Current.CancellationToken);
        await core.WhenRebuiltAsync();
        core.Rebuilt.Should().BeTrue("the service answered the list of manifests");

        var application = new ApplicationInstance { ApplicationName = "DWIS Blackboard live test client", ApplicationType = ApplicationType.Client };
        var configuration = await application.Build("urn:d-wis:blackboard:realtime:livetests", "https://github.com/D-WIS/DWIS-Blackboard-RealTime")
            .AsClient()
            .AddSecurityConfiguration("CN=DWIS Blackboard live test client", Path.Combine(Path.GetTempPath(), "dwis-realtime-tests", "client-pki"))
            .SetAutoAcceptUntrustedCertificates(true)
            .Create();
        await application.CheckApplicationInstanceCertificatesAsync(true, null, CancellationToken.None);
        var endpoint = CoreClientUtils.SelectEndpoint(configuration, _server.EndpointUrl, useSecurity: false);
        using var session = await Session.Create(configuration, new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration)), false, "live", 60_000, new UserIdentity(new AnonymousIdentityToken()), null, CancellationToken.None);
        var serverNs = (ushort)session.NamespaceUris.GetIndex(Naming.ServerNamespace);
        var objectNode = new NodeId(DDHubMethods.ObjectName, serverNs);

        var outputs = session.Call(objectNode, new NodeId(DDHubMethods.Inject, serverNs), new object[] { new[] { ManifestJson } });

        var json = outputs.Should().ContainSingle().Which.Should().BeOfType<string>().Subject;
        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("Success").GetBoolean().Should().BeTrue();
        var mapping = result.RootElement.GetProperty("ProvidedVariables").EnumerateArray().Single();
        var variableNode = new NodeId(mapping.GetProperty("InjectedID").GetProperty("ID").GetString(), mapping.GetProperty("InjectedID").GetProperty("NameSpaceIndex").GetUInt16());
        await session.FetchNamespaceTablesAsync(TestContext.Current.CancellationToken);
        session.ReadValue(variableNode).Value.Should().Be(12.5);
        core.Live.Count.Should().BeGreaterThan(0);

        var deleted = session.Call(objectNode, new NodeId(DDHubMethods.Delete, serverNs), new object[] { new[] { ManifestJson } });

        deleted.Should().ContainSingle().Which.Should().Be(true);
        adapter.ReadValue("RoundTrip", "SPP").Should().BeNull();
        await session.CloseAsync(TestContext.Current.CancellationToken);
    }
}
