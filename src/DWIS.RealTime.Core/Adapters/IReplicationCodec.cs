namespace DWIS.RealTime.Core.Adapters;

/// <summary>
/// The wire format of the hub (decision D-63; R-HUB-1, R-HUB-2): implemented by the adapter, since the values travel
/// as the OPC UA binary encoding of a node id array and a data value array, with the sender's namespace table, as
/// the old <c>RealTimeDataDTO</c> carried them, and Core has no OPC UA type. Both directions are pure: no node is read
/// or written here.
/// </summary>
public interface IReplicationCodec
{
    /// <summary>Encodes a batch of values for the hub: the node ids and the data values in OPC UA binary, the attribute ids, and this process's namespace table.</summary>
    ReplicationBatch Encode(IReadOnlyList<ReplicatedValue> values);

    /// <summary>Decodes a batch from a peer: every value with the namespace URI (looked up in the sender's table) and the node id string of its variable.</summary>
    IReadOnlyList<ReplicatedValue> Decode(ReplicationBatch batch);
}

/// <summary>One replicated value: where the variable is (namespace URI and node id string, as the address space names it), the attribute (13 is Value), the value and its source timestamp.</summary>
public sealed record ReplicatedValue(string NamespaceUri, string NodeId, uint AttributeId, object? Value, DateTime SourceTimestamp)
{
    /// <summary>The attribute id of a variable's value in OPC UA.</summary>
    public const uint ValueAttribute = 13;
}

/// <summary>The four wire fields of the old <c>RealTimeDataDTO</c>: the node id array and the data value array in OPC UA binary, one attribute id per value, and the sender's namespace table (index to URI).</summary>
public sealed record ReplicationBatch(byte[] NodeIds, byte[] Values, uint[] Attributes, string[] Namespaces);
