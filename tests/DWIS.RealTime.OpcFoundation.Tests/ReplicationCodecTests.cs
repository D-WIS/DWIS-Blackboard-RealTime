using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.OpcFoundation.Tests;

/// <summary>The codec of the hub role on the OPC Foundation stack (D-63; R-HUB-2): a batch survives the OPC UA binary wire format, namespace table included.</summary>
public sealed class ReplicationCodecTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _server;

    public ReplicationCodecTests(ServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public void A_batch_round_trips_through_the_binary_encoding_and_the_sender_namespace_table()
    {
        var space = _server.NodeManager;
        space.CreateProviderFolder("Codec");
        var spp = space.CreateVariable("Codec", new VariableSpec("SPP", "double", 0, [], "0", "SPP_validitySignal"));
        var name = space.CreateVariable("Codec", new VariableSpec("Name", "string", 0, [], "", "Name_validitySignal"));
        var t = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
        var values = new List<ReplicatedValue>
        {
            new(spp.NamespaceUri, spp.NodeId, ReplicatedValue.ValueAttribute, 12.5, t),
            new(name.NamespaceUri, name.NodeId, ReplicatedValue.ValueAttribute, "well A", t.AddSeconds(1)),
            new("http://ddhub.no/Nobody/Variables/", "Nobody.x", ReplicatedValue.ValueAttribute, 1.0, t), // a namespace this server never claimed: left out
        };

        var batch = space.Encode(values);
        var back = space.Decode(batch);

        batch.Namespaces.Should().Contain(spp.NamespaceUri).And.NotContain("http://ddhub.no/Nobody/Variables/");
        batch.Attributes.Should().Equal(ReplicatedValue.ValueAttribute, ReplicatedValue.ValueAttribute);
        batch.NodeIds.Should().NotBeEmpty();
        batch.Values.Should().NotBeEmpty();
        back.Should().HaveCount(2);
        back[0].Should().Be(new ReplicatedValue(spp.NamespaceUri, spp.NodeId, ReplicatedValue.ValueAttribute, 12.5, t));
        back[1].NamespaceUri.Should().Be(name.NamespaceUri);
        back[1].NodeId.Should().Be("Codec.Name");
        back[1].Value.Should().Be("well A");
        back[1].SourceTimestamp.Should().Be(t.AddSeconds(1));
        space.DeleteProviderFolder("Codec");
    }

    [Fact]
    public void A_peers_table_is_read_by_its_own_indexes_not_ours()
    {
        // A batch as a peer with another namespace order would send it: index 7 means its seventh URI, whatever ours is.
        var space = _server.NodeManager;
        space.CreateProviderFolder("Peer");
        var address = space.CreateVariable("Peer", new VariableSpec("ROP", "double", 0, [], "0", "ROP_validitySignal"));
        var batch = space.Encode([new ReplicatedValue(address.NamespaceUri, address.NodeId, ReplicatedValue.ValueAttribute, 3.0, DateTime.UtcNow)]);
        var ourIndex = Array.IndexOf(batch.Namespaces, address.NamespaceUri);
        ourIndex.Should().BeGreaterThan(0);
        // The peer's table: the same URIs but with the variables namespace shifted by a filler entry, so the index differs.
        var peerTable = batch.Namespaces.Take(ourIndex).Append("urn:filler").Concat(batch.Namespaces.Skip(ourIndex)).ToArray();
        // Re-encode against that table by decoding our batch and encoding nothing: the point is the decode side, so build the batch by hand.
        var shifted = new ReplicationBatch(batch.NodeIds, batch.Values, batch.Attributes, peerTable);

        var back = space.Decode(shifted);

        back.Should().ContainSingle().Which.NamespaceUri.Should().Be("urn:filler", "the index in the node id names the peer's seventh URI, which is the filler now");
        space.DeleteProviderFolder("Peer");
    }
}
