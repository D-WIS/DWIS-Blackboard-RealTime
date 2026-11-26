using System.Text.Json;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Injection;

namespace DWIS.RealTime.Core.Tests;

/// <summary>The injection side of the façade against the test double and the fake service (M4.1; R-BC-1, R-RT-2, R-RT-4, R-RT-6, D-52).</summary>
public sealed class InjectionTests : IAsyncDisposable
{
    private readonly TestDoubleAdapter _double = new();
    private readonly FakeSemanticService _semantic = new();
    private readonly RealTimeCore _core;

    public InjectionTests()
    {
        _core = new RealTimeCore(_double, _double, _double, _semantic, new RealTimeOptions { RetrySeconds = 1 });
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    /// <summary>A manifest JSON with the given provided variables (id, type, initial value).</summary>
    private static string Manifest(string provider, string name, params (string Id, string Type, string Initial)[] variables) =>
        JsonSerializer.Serialize(new
        {
            ManifestName = name,
            Provider = new { Name = provider, Company = "Acme Ltd" },
            InjectionInformation = new { InjectedNodesNamespaceAlias = "nodes", ProvidedVariablesNamespaceAlias = "pv" },
            ProvidedVariables = variables.Select(v => new { VariableID = v.Id, DataType = v.Type, Rank = 0, InitialValue = v.Initial }).ToList(),
            InjectedNodes = Array.Empty<object>(),
            InjectedReferences = Array.Empty<object>(),
        });

    private static readonly string Rig = Manifest("Acme", "Rig signals", ("SPP", "double", "1.5"), ("WOB", "float", ""));

    private async Task StartAsync()
    {
        await _core.StartAsync(TestContext.Current.CancellationToken);
        await _core.WhenRebuiltAsync();
    }

    [Fact]
    public async Task Inject_creates_the_folder_the_variables_and_their_validity_booleans_then_commits_with_their_addresses()
    {
        await StartAsync();

        var result = _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig);

        result.Status.Should().Be(MethodStatus.Good);
        _semantic.Calls.Where(c => c != "watch").Should().Equal("list", "list", "prepare", "commit");
        _double.Providers.Should().Equal("Acme");
        _double.Variables.Keys.Should().BeEquivalentTo([("Acme", "SPP"), ("Acme", "WOB")]);
        _double.Variables[("Acme", "SPP")].Spec.ValidityVariableId.Should().Be("SPP_validitySignal");
        _double.Variables[("Acme", "SPP")].Value.Should().Be(1.5);
        // The service lists the validity booleans as variables of their own and gets an endpoint for each (T-RT-4);
        // the adapter creates them next to their variable, not a second time.
        var endpoints = _semantic.Live["Acme/rig-signals"].Endpoints;
        endpoints.Select(e => (e.VariableId, e.NamespaceUri, e.NodeId, e.EndpointUrl)).Should().Equal(
            ("SPP", "http://ddhub.no/Acme/Variables/", "Acme.SPP", TestDoubleAdapter.EndpointUrl),
            ("WOB", "http://ddhub.no/Acme/Variables/", "Acme.WOB", TestDoubleAdapter.EndpointUrl),
            ("SPP_validitySignal", "http://ddhub.no/Acme/Variables/", "Acme.SPP_validitySignal", TestDoubleAdapter.EndpointUrl),
            ("WOB_validitySignal", "http://ddhub.no/Acme/Variables/", "Acme.WOB_validitySignal", TestDoubleAdapter.EndpointUrl));
        _core.Live.Get(new ManifestKey("Acme", "rig-signals"))!.VariableIds.Should().Equal("SPP", "WOB");
    }

    [Fact]
    public async Task A_name_with_a_blank_is_claimed_with_an_underscore_and_answered_as_written()
    {
        // Decision D-62: the old server's own CLI example has the provider "DDHub CLI". The namespaces and node ids
        // carry the safe form, the same the semantic service puts in its IRIs; the answer names the manifest's own ids.
        await StartAsync();
        var cli = Manifest("DDHub CLI", "Simple Example", ("Hook Load", "double", "2"), ("SPP", "double", "1"));

        var result = _double.Call(DDHubMethods.Inject, new SessionId("s"), cli);

        result.Status.Should().Be(MethodStatus.Good);
        _double.Providers.Should().Equal("DDHub_CLI");
        _double.Variables.Keys.Should().BeEquivalentTo([("DDHub_CLI", "Hook_Load"), ("DDHub_CLI", "SPP")]);
        var endpoints = _semantic.Live["DDHub_CLI/simple-example"].Endpoints;
        endpoints.Select(e => (e.VariableId, e.NamespaceUri, e.NodeId)).Should().Contain(("Hook_Load", "http://ddhub.no/DDHub_CLI/Variables/", "DDHub_CLI.Hook_Load"));
        using var document = JsonDocument.Parse((string)result.Outputs[0]!);
        var root = document.RootElement;
        root.GetProperty("InjectedNodesNamespace").GetString().Should().Be("http://ddhub.no/DDHub_CLI/");
        root.GetProperty("ProvidedVariablesNamespace").GetString().Should().Be("http://ddhub.no/DDHub_CLI/Variables/");
        var provided = root.GetProperty("ProvidedVariables").EnumerateArray().ToList();
        provided.Should().HaveCount(2);
        provided[0].GetProperty("ManifestItemID").GetString().Should().Be("Hook Load", "the client matches the answer against its own manifest");
        provided[0].GetProperty("InjectedID").GetProperty("ID").GetString().Should().Be("DDHub_CLI.Hook_Load");
        _core.Live.Get(new ManifestKey("DDHub_CLI", "simple-example")).Should().NotBeNull();
        ManifestKey.Of(DWIS.Blackboard.Contracts.Manifests.ManifestJson.Parse(cli))!.Value.Should().Be("DDHub_CLI/simple-example");
    }

    [Fact]
    public async Task A_manifest_that_declares_its_own_validity_booleans_gets_them_once_and_listed_in_the_answer()
    {
        // The old client library added the booleans to the manifest before sending it; the old server then created
        // and listed them as provided variables. Same answer here, and still one node per boolean.
        await StartAsync();
        var withFlags = Manifest("Acme", "Rig signals", ("SPP", "double", "1.5"), ("SPP_validitySignal", "bool", "true"), ("WOB", "float", ""), ("WOB_validitySignal", "bool", "true"));

        var result = _double.Call(DDHubMethods.Inject, new SessionId("s"), withFlags);

        result.Status.Should().Be(MethodStatus.Good);
        _double.Variables.Keys.Should().BeEquivalentTo([("Acme", "SPP"), ("Acme", "WOB")]);
        _semantic.Live["Acme/rig-signals"].Endpoints.Select(e => e.VariableId).Should().BeEquivalentTo("SPP", "SPP_validitySignal", "WOB", "WOB_validitySignal");
        using var document = JsonDocument.Parse((string)result.Outputs[0]!);
        document.RootElement.GetProperty("ProvidedVariables").EnumerateArray().Select(m => m.GetProperty("ManifestItemID").GetString())
            .Should().Equal("SPP", "SPP_validitySignal", "WOB", "WOB_validitySignal");
    }

    [Fact]
    public async Task The_result_json_has_the_old_shape_with_the_namespace_index_and_node_id_of_each_variable()
    {
        await StartAsync();

        var json = (string)_double.Call(DDHubMethods.Inject, new SessionId("s"), Rig).Outputs[0]!;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("Success", "InjectedVariableNamespace", "InjectedNodesNamespace", "ProvidedVariablesNamespace", "InjectedVariables", "InjectedNodes", "ProvidedVariables");
        root.GetProperty("Success").GetBoolean().Should().BeTrue();
        root.GetProperty("InjectedVariableNamespace").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("InjectedVariables").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("InjectedNodesNamespace").GetString().Should().Be("http://ddhub.no/Acme/");
        root.GetProperty("InjectedNodes").GetArrayLength().Should().Be(0);
        root.GetProperty("ProvidedVariablesNamespace").GetString().Should().Be("http://ddhub.no/Acme/Variables/");
        var provided = root.GetProperty("ProvidedVariables").EnumerateArray().ToList();
        provided.Should().HaveCount(2);
        provided[0].GetProperty("ManifestItemID").GetString().Should().Be("SPP");
        provided[0].GetProperty("InjectedID").GetProperty("ID").GetString().Should().Be("Acme.SPP");
        provided[0].GetProperty("InjectedID").GetProperty("NameSpaceIndex").GetUInt16().Should().Be(_double.AddressFor("Acme", "SPP").NamespaceIndex);
        json.Should().StartWith("{\n  \"Success\": true,");
    }

    [Fact]
    public async Task The_same_manifest_again_is_already_live_and_creates_nothing_new()
    {
        await StartAsync();
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig);
        var before = _double.Variables.Count;
        _semantic.Calls.Clear();

        var again = _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig);

        again.Status.Should().Be(MethodStatus.Good);
        _semantic.Calls.Should().Equal("prepare");
        _double.Variables.Should().HaveCount(before);
    }

    [Fact]
    public async Task Update_replaces_the_variables_and_keeps_the_live_ones_when_the_commit_fails()
    {
        await StartAsync();
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig);
        var changed = Manifest("Acme", "Rig signals", ("SPP", "double", "2"), ("ROP", "double", ""));

        _semantic.FailNextCommit = MethodStatus.BadCommunicationError;
        var failed = _double.Call(DDHubMethods.Update, new SessionId("s"), changed);

        failed.Status.Should().Be(MethodStatus.BadCommunicationError);
        _double.Variables.Keys.Should().BeEquivalentTo([("Acme", "SPP"), ("Acme", "WOB")], "D-52 rule 1: the nodes of the live version stay; the new one goes");
        _semantic.Calls.Should().EndWith("abort");
        _semantic.Live.Should().ContainKey("Acme/rig-signals");

        var ok = _double.Call(DDHubMethods.Update, new SessionId("s"), changed);

        ok.Status.Should().Be(MethodStatus.Good);
        _double.Variables.Keys.Should().BeEquivalentTo([("Acme", "SPP"), ("Acme", "ROP")], "the variable the new version lacks is deleted after the commit");
        _core.Live.Get(new ManifestKey("Acme", "rig-signals"))!.VariableIds.Should().Equal("SPP", "ROP");
    }

    [Fact]
    public async Task A_prepare_that_fails_leaves_the_address_space_as_it_was()
    {
        await StartAsync();
        _semantic.RefusePrepare = true;

        var result = _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig);

        result.Status.Should().Be(MethodStatus.BadInvalidArgument);
        _double.Providers.Should().BeEmpty();
        _double.Variables.Should().BeEmpty();
        _core.Live.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_commit_of_a_fresh_manifest_removes_its_nodes_and_aborts()
    {
        await StartAsync();
        _semantic.FailNextCommit = MethodStatus.BadCommunicationError;

        var result = _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig);

        result.Status.Should().Be(MethodStatus.BadCommunicationError);
        _double.Providers.Should().BeEmpty();
        _double.Variables.Should().BeEmpty();
        _semantic.Calls.Should().EndWith("abort");
    }

    [Fact]
    public async Task Not_a_manifest_or_no_names_answers_an_invalid_argument()
    {
        await StartAsync();

        _double.Call(DDHubMethods.Inject, new SessionId("s"), "not json").Status.Should().Be(MethodStatus.BadInvalidArgument);
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Manifest("", "no provider")).Status.Should().Be(MethodStatus.BadInvalidArgument);
        _double.Call(DDHubMethods.Delete, new SessionId("s"), "[]").Status.Should().Be(MethodStatus.BadInvalidArgument);
        _semantic.Calls.Where(c => c != "watch").Should().Equal("list", "list");
    }

    [Fact]
    public async Task Delete_removes_the_manifest_its_variables_and_the_folder_when_it_was_the_providers_last()
    {
        await StartAsync();
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig);
        var second = Manifest("Acme", "More signals", ("ROP", "double", ""));
        _double.Call(DDHubMethods.Inject, new SessionId("s"), second);

        var first = _double.Call(DDHubMethods.Delete, new SessionId("s"), Rig);

        first.Status.Should().Be(MethodStatus.Good);
        first.Outputs.Should().ContainSingle().Which.Should().Be(true);
        _double.Variables.Keys.Should().BeEquivalentTo([("Acme", "ROP")]);
        _double.Providers.Should().ContainSingle("another manifest of the provider is live").Which.Should().Be("Acme");

        _double.Call(DDHubMethods.Delete, new SessionId("s"), second).Outputs[0].Should().Be(true);
        _double.Providers.Should().BeEmpty();
        _double.Call(DDHubMethods.Delete, new SessionId("s"), second).Outputs[0].Should().Be(false, "it was not live");
    }

    [Fact]
    public async Task While_the_service_is_down_the_methods_fail_fast_with_a_communication_error()
    {
        _semantic.Down = MethodStatus.BadCommunicationError;
        await _core.StartAsync(TestContext.Current.CancellationToken);

        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig).Status.Should().Be(MethodStatus.BadCommunicationError);
        _double.Call(DDHubMethods.Delete, new SessionId("s"), Rig).Status.Should().Be(MethodStatus.BadCommunicationError);
        _double.Variables.Should().BeEmpty();
        _core.Rebuilt.Should().BeFalse();

        _semantic.Down = null;
        await _core.WhenRebuiltAsync();
        _core.Rebuilt.Should().BeTrue("the rebuild loop keeps asking until the service answers");
        await _core.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_rebuilds_the_address_space_from_the_service_and_lists_once_more()
    {
        var first = new FakeSemanticService();
        var firstCore = new RealTimeCore(new TestDoubleAdapter(), new TestDoubleAdapter(), new TestDoubleAdapter(), first);
        await firstCore.Injector.InjectAsync(Rig, TestContext.Current.CancellationToken);
        await firstCore.Injector.InjectAsync(Manifest("Beta", "Model", ("ECD", "double", "")), TestContext.Current.CancellationToken);
        var restarted = new RealTimeCore(_double, _double, _double, first, new RealTimeOptions { RetrySeconds = 1 });

        await restarted.StartAsync(TestContext.Current.CancellationToken);
        await restarted.WhenRebuiltAsync();

        first.Calls.Where(c => c != "watch").Should().EndWith("list", "list");
        _double.Providers.Should().BeEquivalentTo("Acme", "Beta");
        _double.Variables.Keys.Should().BeEquivalentTo([("Acme", "SPP"), ("Acme", "WOB"), ("Beta", "ECD")]);
        _double.Variables[("Acme", "SPP")].Value.Should().Be(1.5);
        restarted.Live.Count.Should().Be(2);
        restarted.Live.Get(new ManifestKey("Beta", "model"))!.VariableIds.Should().Equal("ECD");
        await restarted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_manifest_key_makes_the_name_safe_as_the_service_does()
    {
        ManifestKey.Slug("SmartConnect rig signals").Should().Be("smartconnect-rig-signals");
        ManifestKey.Slug("  Yggdrasil 25/1-14 G  ").Should().Be("yggdrasil-25-1-14-g");
        ManifestKey.Slug("!!!").Should().BeEmpty();
        ManifestKey.Parse(Manifest("P", "A b")).Key!.Value.Should().Be("P/a-b");
    }
}
