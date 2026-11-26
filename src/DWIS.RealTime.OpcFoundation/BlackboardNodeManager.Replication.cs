using DWIS.RealTime.Core.Adapters;
using Opc.Ua;

namespace DWIS.RealTime.OpcFoundation;

/// <summary>
/// The two interfaces of the hub role (decision D-63): the codec of the wire format, as the old
/// <c>DDHubServerManager</c> wrote and read it with the OPC Foundation stack (a node id array and a data value
/// array in OPC UA binary, cross-decodable with the Unified Automation stack, R-HUB-2), and the word to Core when
/// a client writes a provided variable.
/// </summary>
public sealed partial class BlackboardNodeManager : IReplicationCodec
{
    private const string NodeIdsField = "n";
    private const string ValuesField = "v";

    /// <summary>Where client writes go (Core); null while no hub role runs.</summary>
    public IValueEvents? ValueEvents { get; set; }

    /// <inheritdoc />
    public ReplicationBatch Encode(IReadOnlyList<ReplicatedValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var context = Server.MessageContext;
        var nodeIds = new NodeIdCollection();
        var dataValues = new DataValueCollection();
        var attributes = new List<uint>();
        foreach (var value in values)
        {
            var index = Server.NamespaceUris.GetIndex(value.NamespaceUri);
            if (index < 0)
            {
                continue;
            }
            nodeIds.Add(new NodeId(value.NodeId, (ushort)index));
            dataValues.Add(new DataValue(new Variant(value.Value), StatusCodes.Good, value.SourceTimestamp, DateTime.UtcNow));
            attributes.Add(value.AttributeId);
        }
        byte[] ids;
        using (var encoder = new BinaryEncoder(context))
        {
            encoder.WriteNodeIdArray(NodeIdsField, nodeIds);
            ids = encoder.CloseAndReturnBuffer();
        }
        byte[] vals;
        using (var encoder = new BinaryEncoder(context))
        {
            encoder.WriteDataValueArray(ValuesField, dataValues);
            vals = encoder.CloseAndReturnBuffer();
        }
        return new ReplicationBatch(ids, vals, [.. attributes], Server.NamespaceUris.ToArray());
    }

    /// <inheritdoc />
    public IReadOnlyList<ReplicatedValue> Decode(ReplicationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        // The sender's namespace table: its indexes are not ours, so every id is resolved to a URI here.
        var table = new NamespaceTable();
        table.Update(batch.Namespaces);
        var context = new ServiceMessageContext(Server.Telemetry) { NamespaceUris = table };
        NodeIdCollection ids;
        using (var decoder = new BinaryDecoder(batch.NodeIds, context))
        {
            ids = decoder.ReadNodeIdArray(NodeIdsField);
        }
        DataValueCollection values;
        using (var decoder = new BinaryDecoder(batch.Values, context))
        {
            values = decoder.ReadDataValueArray(ValuesField);
        }
        var result = new List<ReplicatedValue>(ids.Count);
        for (var i = 0; i < ids.Count && i < values.Count; i++)
        {
            var uri = ids[i].NamespaceIndex < batch.Namespaces.Length ? batch.Namespaces[ids[i].NamespaceIndex] : "";
            var attribute = i < batch.Attributes.Length ? batch.Attributes[i] : ReplicatedValue.ValueAttribute;
            result.Add(new ReplicatedValue(uri, ids[i].Identifier?.ToString() ?? "", attribute, values[i].Value, values[i].SourceTimestamp));
        }
        return result;
    }

    /// <summary>The write handler of a provided variable: a client's write is reported to Core before the stack stores it.</summary>
    private ServiceResult OnClientWrite(string provider, string variableId, ref object value, ref DateTime timestamp)
    {
        var events = ValueEvents;
        if (events is not null)
        {
            var sourceTimestamp = timestamp == DateTime.MinValue ? DateTime.UtcNow : timestamp;
            events.ValueWritten(provider, variableId, value, sourceTimestamp);
        }
        return ServiceResult.Good;
    }
}
