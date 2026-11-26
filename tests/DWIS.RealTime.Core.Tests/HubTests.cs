using System.Text.Json;
using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Core.Hub;

namespace DWIS.RealTime.Core.Tests;

/// <summary>The hub role against the test double, the fake service and a fake hub (M5.2; R-HUB-1 to R-HUB-3, D-63).</summary>
public sealed class HubTests : IAsyncDisposable
{
    private readonly TestDoubleAdapter _double = new();
    private readonly FakeSemanticService _semantic = new();
    private readonly FakeHubConnection _hub = new();
    private readonly RealTimeCore _core;

    public HubTests()
    {
        var options = new RealTimeOptions { RetrySeconds = 1 };
        options.Hub.Url = "http://hub:5000";
        options.Hub.Group = "rig-7";
        options.Hub.Site = "site-a";
        options.Hub.Password = "secret";
        options.Hub.BatchMilliseconds = 50;
        _core = new RealTimeCore(_double, _double, _double, _semantic, options, null, new FakeCodec(), _hub);
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    private static string Manifest(string provider, string name, params string[] variables) =>
        JsonSerializer.Serialize(new
        {
            ManifestName = name,
            Provider = new { Name = provider, Company = "Acme Ltd" },
            InjectionInformation = new { InjectedNodesNamespaceAlias = "nodes", ProvidedVariablesNamespaceAlias = "pv" },
            ProvidedVariables = variables.Select(v => new { VariableID = v, DataType = "double", Rank = 0, InitialValue = "0" }).ToList(),
            InjectedNodes = Array.Empty<object>(),
            InjectedReferences = Array.Empty<object>(),
        });

    private static readonly string Rig = Manifest("Acme", "Rig signals", "SPP", "WOB");

    private async Task StartAsync()
    {
        await _core.StartAsync(TestContext.Current.CancellationToken);
        await _core.WhenRebuiltAsync();
        await Eventually(() => _core.Hub!.Connected, "the hub role connects and registers");
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task Without_a_hub_url_there_is_no_hub_role()
    {
        await using var core = new RealTimeCore(_double, _double, _double, _semantic, new RealTimeOptions(), null, new FakeCodec(), new FakeHubConnection());
        core.Hub.Should().BeNull();
        core.ValueWritten("Acme", "SPP", 1.0, DateTime.UtcNow); // nothing to do, nothing thrown
    }

    [Fact]
    public async Task Connects_registers_to_the_group_and_takes_the_manifests_the_hub_kept()
    {
        var peer = Guid.NewGuid();
        _hub.Kept.Add(new HubManifests(peer, [Manifest("Peer", "Peer signals", "ROP")]));

        await StartAsync();

        _hub.Registrations.Should().Equal(("rig-7", "secret"));
        _core.Hub!.CanWrite.Should().BeTrue();
        _semantic.Live.Should().ContainKey("Peer/peer-signals");
        _semantic.Origins["Peer/peer-signals"].Should().Be($"hub:rig-7:{peer}", "the provenance says where a peer's manifest came from (R-HUB-3)");
        _double.Variables.Keys.Should().Contain(("Peer", "ROP"), "a peer's manifest goes through the same injection path as a local one");
        _core.Hub.ManifestsReceived.Should().Be(1);
    }

    [Fact]
    public async Task Client_writes_are_batched_with_the_latest_value_per_variable_and_sent_under_this_sender()
    {
        await StartAsync();
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig).Status.Should().Be(MethodStatus.Good);
        await _core.Hub!.FlushAsync(TestContext.Current.CancellationToken); // the manifest that went live goes out; the values start empty
        var t1 = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

        // The adapter reports three client writes, two of them to the same variable, within one batch period.
        _core.ValueWritten("Acme", "SPP", 1.0, t1);
        _core.ValueWritten("Acme", "WOB", 7.5, t1);
        _core.ValueWritten("Acme", "SPP", 2.0, t1.AddSeconds(1));
        var (values, manifests) = await _core.Hub.FlushAsync(TestContext.Current.CancellationToken);

        values.Should().Be(2);
        manifests.Should().Be(0);
        var batch = _hub.SignalsSent.Should().ContainSingle().Subject;
        batch.Sender.Should().Be(_core.Hub.Sender);
        var sent = FakeCodec.ValuesOf(batch);
        sent.Should().HaveCount(2).And.OnlyContain(v => v.AttributeId == ReplicatedValue.ValueAttribute && v.NamespaceUri == "http://ddhub.no/Acme/Variables/");
        sent.Single(v => v.NodeId == "Acme.SPP").Value.Should().Be(2.0, "the latest value of a variable wins within a batch");
        sent.Single(v => v.NodeId == "Acme.SPP").SourceTimestamp.Should().Be(t1.AddSeconds(1));
        sent.Single(v => v.NodeId == "Acme.WOB").Value.Should().Be(7.5);
        (await _core.Hub.FlushAsync(TestContext.Current.CancellationToken)).Values.Should().Be(0, "a flush empties the buffer");
    }

    [Fact]
    public async Task A_peers_values_are_written_to_the_local_variables_and_its_own_echo_is_dropped()
    {
        await StartAsync();
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig).Status.Should().Be(MethodStatus.Good);
        var codec = new FakeCodec();
        var t = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
        var peer = Guid.NewGuid();
        var batch = codec.Encode(
        [
            new ReplicatedValue("http://ddhub.no/Acme/Variables/", "Acme.SPP", ReplicatedValue.ValueAttribute, 42.0, t),
            new ReplicatedValue("http://ddhub.no/Acme/Variables/", "Acme.Unknown", ReplicatedValue.ValueAttribute, 1.0, t),
            new ReplicatedValue("http://ddhub.no/Other/Variables/", "Other.SPP", ReplicatedValue.ValueAttribute, 1.0, t),
        ]);

        await _hub.PeerSendsAsync(new HubSignals(peer, batch));

        _double.Variables[("Acme", "SPP")].Value.Should().Be(42.0);
        _double.Variables[("Acme", "SPP")].SourceTimestamp.Should().Be(t);
        _core.Hub!.ValuesReceived.Should().Be(1, "a value for a variable that is not here is dropped");
        (await _core.Hub.FlushAsync(TestContext.Current.CancellationToken)).Values.Should().Be(0, "a peer's value is never sent back (the adapter raises nothing for Core's own writes)");

        await _hub.PeerSendsAsync(new HubSignals(_core.Hub.Sender, codec.Encode([new ReplicatedValue("http://ddhub.no/Acme/Variables/", "Acme.SPP", ReplicatedValue.ValueAttribute, 99.0, t)])));
        _double.Variables[("Acme", "SPP")].Value.Should().Be(42.0, "this process's own batch comes back from the hub and is dropped");
    }

    [Fact]
    public async Task A_local_manifest_goes_out_and_a_peers_manifest_is_injected_with_its_origin_but_not_sent_back()
    {
        await StartAsync();
        var peer = Guid.NewGuid();

        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig).Status.Should().Be(MethodStatus.Good);
        await _hub.PeerSendsAsync(new HubManifests(peer, [Manifest("Peer", "Peer signals", "ROP")]));
        var (_, manifests) = await _core.Hub!.FlushAsync(TestContext.Current.CancellationToken);

        manifests.Should().Be(1, "the local manifest only");
        var sent = _hub.ManifestsSent.Should().ContainSingle().Subject;
        sent.Sender.Should().Be(_core.Hub.Sender);
        sent.ManifestJsons.Should().Equal(Rig);
        _semantic.Origins["Acme/rig-signals"].Should().Be("local");
        _semantic.Origins["Peer/peer-signals"].Should().Be($"hub:rig-7:{peer}");
        _double.Variables.Keys.Should().Contain(("Peer", "ROP"));

        // The same local manifest again is live already: nothing new goes out.
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig).Status.Should().Be(MethodStatus.Good);
        (await _core.Hub.FlushAsync(TestContext.Current.CancellationToken)).Manifests.Should().Be(0);
    }

    [Fact]
    public async Task A_reconnection_registers_to_the_group_again_and_a_member_without_write_rights_sends_nothing()
    {
        _hub.WriteRights = false;
        await StartAsync();
        _core.Hub!.CanWrite.Should().BeFalse();
        _core.ValueWritten("Acme", "SPP", 1.0, DateTime.UtcNow);
        (await _core.Hub.FlushAsync(TestContext.Current.CancellationToken)).Values.Should().Be(0, "no write rights: the values are not even buffered");

        _hub.WriteRights = true;
        await _hub.ReconnectAsync();

        _hub.Registrations.Should().HaveCount(2);
        _core.Hub.CanWrite.Should().BeTrue();
    }

    [Fact]
    public async Task The_send_loop_runs_on_its_own_and_a_hub_that_is_down_is_tried_again()
    {
        _hub.Down = true;
        await _core.StartAsync(TestContext.Current.CancellationToken);
        await _core.WhenRebuiltAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        _core.Hub!.Connected.Should().BeFalse();

        _hub.Down = false;
        await Eventually(() => _core.Hub.Connected, "the role connects after the retry delay");
        _double.Call(DDHubMethods.Inject, new SessionId("s"), Rig).Status.Should().Be(MethodStatus.Good);
        _core.ValueWritten("Acme", "SPP", 3.0, DateTime.UtcNow);
        await Eventually(() => _hub.SignalsSent.Count >= 1 && _hub.ManifestsSent.Count >= 1, "the loop sends the buffered values and manifest within the batch period");
    }
}
