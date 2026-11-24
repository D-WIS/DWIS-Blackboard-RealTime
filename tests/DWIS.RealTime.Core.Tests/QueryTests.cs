using System.Text.Json;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Queries;
using static DWIS.RealTime.Core.Tests.FakeSemanticService;

namespace DWIS.RealTime.Core.Tests;

/// <summary>The query side of the façade against the test double and the fake service (M4.2; R-QR-1 to R-QR-3, D-31, D-53, T-QR-3, T-QR-4).</summary>
public sealed class QueryTests : IAsyncDisposable
{
    private const string Spp = "SELECT ?s WHERE { ?s a <http://ddhub.no/SPPressure> }";
    private static readonly SessionId A = new("ns=1;i=11");
    private static readonly SessionId B = new("ns=1;i=22");

    private readonly TestDoubleAdapter _double = new();
    private readonly FakeSemanticService _semantic = new();
    private readonly RealTimeCore _core;

    public QueryTests()
    {
        _semantic.Answers[Spp] = (["s"], [RowOf("http://d-wis.org/Acme/Variables/Acme.SPP")]);
        _core = new RealTimeCore(_double, _double, _double, _semantic, new RealTimeOptions { RetrySeconds = 1 });
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    private async Task StartAsync()
    {
        await _core.StartAsync(TestContext.Current.CancellationToken);
        await _core.WhenRebuiltAsync();
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task Resolve_rewrites_the_namespaces_and_answers_the_old_query_result_shape()
    {
        await StartAsync();
        _semantic.Answers["q"] = (["dp", "n"], [RowOf("http://d-wis.org/Acme/StandpipePressure", "12.5"), RowOf("http://d-wis.org/Acme/Variables/Acme.SPP", "http://d-wis.org/MudStandpipe")]);

        var result = _double.Call(DDHubMethods.ResolveQuery, A, "q");

        result.Status.Should().Be(MethodStatus.Good);
        using var document = JsonDocument.Parse((string)result.Outputs[0]!);
        var root = document.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("VariablesHeader", "Results", "Count", "IsReadOnly");
        root.GetProperty("VariablesHeader").EnumerateArray().Select(v => v.GetString()).Should().Equal("?DP", "?N", "the old server's OWL engine answered the names in upper case with the question mark (T-BC-3)");
        var rows = root.GetProperty("Results").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows[0].EnumerateObject().Select(p => p.Name).Should().Equal("Items", "Count", "IsReadOnly");
        var first = rows[0].GetProperty("Items").EnumerateArray().ToList();
        first[0].GetProperty("NameSpace").GetString().Should().Be("http://ddhub.no/Acme/");
        first[0].GetProperty("ID").GetString().Should().Be("StandpipePressure");
        first[1].GetProperty("NameSpace").GetString().Should().BeEmpty("a literal has no namespace");
        first[1].GetProperty("ID").GetString().Should().Be("12.5");
        var second = rows[1].GetProperty("Items").EnumerateArray().ToList();
        second[0].GetProperty("NameSpace").GetString().Should().Be("http://ddhub.no/Acme/Variables/");
        second[0].GetProperty("ID").GetString().Should().Be("Acme.SPP");
        second[1].GetProperty("NameSpace").GetString().Should().Be("http://ddhub.no/", "a vocabulary term is the plain pair too (D-31)");
        root.GetProperty("Count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Get_resources_answers_the_old_resource_file_shape()
    {
        await StartAsync();
        _semantic.Resources.Individuals.Add(new Individual { Iri = "http://d-wis.org/Acme/SPPPoint", TypeIri = "http://d-wis.org/DrillingDataPoint" });
        _semantic.Resources.Individuals.Add(new Individual { Iri = "http://d-wis.org/Acme/SPPPoint", TypeIri = "http://d-wis.org/Measurement" });
        _semantic.Resources.Sentences.Add(new Sentence { Subject = "http://d-wis.org/Acme/SPPPoint", Verb = "http://d-wis.org/HasDynamicValue", Object = "http://d-wis.org/Acme/Variables/Acme.SPP" });

        var result = _double.Call(DDHubMethods.GetResources, A);

        result.Status.Should().Be(MethodStatus.Good);
        using var document = JsonDocument.Parse((string)result.Outputs[0]!);
        var root = document.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("DDHubIndividuals", "DDHubSentences");
        var individuals = root.GetProperty("DDHubIndividuals").EnumerateArray().ToList();
        individuals.Should().HaveCount(1, "one entry per resource, as the old server listed them");
        individuals[0].GetProperty("NodeIdentifier").GetProperty("ID").GetString().Should().Be("SPPPoint");
        individuals[0].GetProperty("TypeDictionaryURI").GetString().Should().BeEmpty();
        var sentence = root.GetProperty("DDHubSentences").EnumerateArray().Single();
        sentence.GetProperty("Verb").GetString().Should().Be("http://ddhub.no/HasDynamicValue");
        sentence.GetProperty("Object").GetProperty("NodeIdentifier").GetProperty("NameSpace").GetString().Should().Be("http://ddhub.no/Acme/Variables/");
    }

    [Fact]
    public async Task Register_creates_the_sessions_nodes_and_answers_the_snapshot_as_the_first_diff()
    {
        await StartAsync();

        var result = _double.Call(DDHubMethods.RegisterQuery, A, Spp);

        result.Status.Should().Be(MethodStatus.Good);
        result.Outputs.Should().HaveCount(2);
        var address = _double.CreateQueryNodes(1, A);
        result.Outputs[1].Should().Be($"ns={address.NamespaceIndex};s={Naming.QueryClientNodeId(1, A)}", "the client node's id, as the old server answered it");
        using var document = JsonDocument.Parse((string)result.Outputs[0]!);
        var root = document.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("QueryID", "QueryResultID", "Added", "Removed");
        root.GetProperty("QueryID").GetString().Should().Be(Naming.QueryClientNodeId(1, A));
        root.GetProperty("QueryResultID").GetString().Should().Be($"ns={address.NamespaceIndex};s={Naming.QueryDiffNodeId(1, A)}", "the client parses it and subscribes to that node");
        root.GetProperty("Added").EnumerateArray().Single().GetProperty("Items").EnumerateArray().Single().GetProperty("ID").GetString().Should().Be("Acme.SPP");
        root.GetProperty("Removed").GetArrayLength().Should().Be(0);
        _double.QueryNodes.Keys.Should().BeEquivalentTo([(1, A)]);
        _double.Diagnostics[DDHubMethods.RegisteredQueryCount].Should().Be(1);
        _double.Diagnostics[DDHubMethods.RegisteredQuerySubscriptionCount].Should().Be(1);
        _semantic.Owners[_semantic.IdOf(Spp)].Should().BeEquivalentTo([A.Value]);
    }

    [Fact]
    public async Task Two_sessions_on_one_query_have_their_own_nodes_and_both_get_the_diff()
    {
        await StartAsync();
        _double.Call(DDHubMethods.RegisterQuery, A, Spp);
        _double.Call(DDHubMethods.RegisterQuery, B, Spp);
        _double.QueryNodes.Keys.Should().BeEquivalentTo([(1, A), (1, B)]);
        _double.Diagnostics[DDHubMethods.RegisteredQueryCount].Should().Be(1);
        _double.Diagnostics[DDHubMethods.RegisteredQuerySubscriptionCount].Should().Be(2);

        _semantic.Push(_semantic.IdOf(Spp), [RowOf("http://d-wis.org/Acme/Variables/Acme.SPP2")], []);

        await Eventually(() => _double.QueryNodes[(1, A)] is not null && _double.QueryNodes[(1, B)] is not null, "the diff reaches both sessions");
        using var document = JsonDocument.Parse(_double.QueryNodes[(1, B)]!);
        document.RootElement.GetProperty("QueryID").GetString().Should().Be(Naming.QueryClientNodeId(1, B));
        document.RootElement.GetProperty("QueryResultID").GetString().Should().Be(Naming.QueryDiffNodeId(1, B));
        document.RootElement.GetProperty("Added").EnumerateArray().Single().GetProperty("Items").EnumerateArray().Single().GetProperty("ID").GetString().Should().Be("Acme.SPP2");
        _core.Queries.Registry.All.Single().Rows.Should().HaveCount(2, "the rows follow the diffs");
    }

    [Fact]
    public async Task Unregister_by_one_session_leaves_the_other_and_a_session_close_releases_its_queries()
    {
        await StartAsync();
        _double.Call(DDHubMethods.RegisterQuery, A, Spp);
        _double.Call(DDHubMethods.RegisterQuery, B, Spp);
        _double.Call(DDHubMethods.RegisterQuery, B, "other");

        var released = _double.Call(DDHubMethods.UnregisterQuery, A, Spp);

        released.Outputs[0].Should().Be(true);
        _double.QueryNodes.Keys.Should().BeEquivalentTo([(1, B), (2, B)]);
        _double.Call(DDHubMethods.UnregisterQuery, A, Spp).Outputs[0].Should().Be(false, "A no longer holds it");
        _double.Diagnostics[DDHubMethods.RegisteredQueryCount].Should().Be(2);

        _core.SessionClosed(B);

        await Eventually(() => _double.QueryNodes.Count == 0 && _semantic.Calls.Contains("release"), "the close takes B's nodes and tells the service");
        _semantic.Owners.Should().BeEmpty();
        _double.Diagnostics[DDHubMethods.RegisteredQueryCount].Should().Be(0);
        _double.Diagnostics[DDHubMethods.RegisteredQuerySubscriptionCount].Should().Be(0);
    }

    [Fact]
    public async Task When_the_stream_ends_the_queries_are_registered_again_and_the_change_since_is_one_diff()
    {
        await StartAsync();
        _double.Call(DDHubMethods.RegisterQuery, A, Spp);
        await Eventually(() => _core.Queries.StreamsOpened == 1, "the stream is open");
        _semantic.Answers[Spp] = (["s"], [RowOf("http://d-wis.org/Acme/Variables/Acme.SPP"), RowOf("http://d-wis.org/Acme/Variables/Acme.SPP3")]);

        _semantic.Restart();

        await Eventually(() => _core.Queries.StreamsOpened == 2, "the loop opens a new stream after the retry delay");
        await Eventually(() => _double.QueryNodes[(1, A)] is not null, "the rows that came while the stream was down arrive as one diff");
        using var document = JsonDocument.Parse(_double.QueryNodes[(1, A)]!);
        document.RootElement.GetProperty("Added").EnumerateArray().Single().GetProperty("Items").EnumerateArray().Single().GetProperty("ID").GetString().Should().Be("Acme.SPP3");
        document.RootElement.GetProperty("Removed").GetArrayLength().Should().Be(0);
        _double.QueryNodes.Keys.Should().BeEquivalentTo([(1, A)], "the nodes stay as they were");
        _semantic.IdOf(Spp).Should().Be(101, "the service gave the query a new id");
        _core.Queries.Registry.All.Single().ServiceId.Should().Be(101);

        _semantic.Push(101, [], [RowOf("http://d-wis.org/Acme/Variables/Acme.SPP")]);
        await Eventually(() => _double.QueryNodes[(1, A)]!.Contains("Removed\": [\n    {", StringComparison.Ordinal), "a diff under the new id still reaches the session's node");
    }

    [Fact]
    public async Task Query_methods_fail_fast_while_the_service_is_down()
    {
        _semantic.Down = MethodStatus.BadCommunicationError;
        await _core.StartAsync(TestContext.Current.CancellationToken);

        _double.Call(DDHubMethods.ResolveQuery, A, Spp).Status.Should().Be(MethodStatus.BadCommunicationError);
        _double.Call(DDHubMethods.GetResources, A).Status.Should().Be(MethodStatus.BadCommunicationError);
        _double.Call(DDHubMethods.RegisterQuery, A, Spp).Status.Should().Be(MethodStatus.BadCommunicationError);
        _double.QueryNodes.Should().BeEmpty();
        _double.Call(DDHubMethods.RegisterQuery, new SessionId(""), Spp).Status.Should().Be(MethodStatus.BadInvalidArgument, "a query needs a session as its owner");
    }

    [Fact]
    public void An_iri_splits_at_its_last_slash_and_a_literal_keeps_its_text()
    {
        QueryJson.Item("http://d-wis.org/Acme/Variables/Acme.SPP").Should().Be(new QueryJson.NodeIdentifierShape("http://ddhub.no/Acme/Variables/", "Acme.SPP"));
        QueryJson.Item("http://ddhub.no/Acme/SPP").Should().Be(new QueryJson.NodeIdentifierShape("http://ddhub.no/Acme/", "SPP"));
        QueryJson.Item("42").Should().Be(new QueryJson.NodeIdentifierShape("", "42"));
        QueryJson.Item("http://d-wis.org/Acme/").Should().Be(new QueryJson.NodeIdentifierShape("", "http://ddhub.no/Acme/"), "a trailing slash leaves no id");
    }
}
