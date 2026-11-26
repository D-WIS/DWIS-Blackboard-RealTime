using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.StubAdapter;

/// <summary>
/// An adapter for no stack (decision D-55): every method throws. It exists so that the three interfaces are
/// implemented twice in this repository, once by the OPC Foundation adapter and once here, with nothing but
/// Core in reach (T-MOD-6). A company starting an adapter for another stack can copy this file as the outline.
/// </summary>
public sealed class StubAdapter : IAddressSpace, IMethodHost, IQueryNodes, IReplicationCodec
{
    private static NotSupportedException NotHere() => new("The stub adapter has no stack behind it (decision D-55).");

    /// <inheritdoc />
    public void CreateProviderFolder(string provider) => throw NotHere();

    /// <inheritdoc />
    public void DeleteProviderFolder(string provider) => throw NotHere();

    /// <inheritdoc />
    public NodeAddress CreateVariable(string provider, VariableSpec spec) => throw NotHere();

    /// <inheritdoc />
    public void DeleteVariable(string provider, string variableId) => throw NotHere();

    /// <inheritdoc />
    public void WriteValue(string provider, string variableId, object? value, DateTime sourceTimestamp) => throw NotHere();

    /// <inheritdoc />
    public TimedValue? ReadValue(string provider, string variableId) => throw NotHere();

    /// <inheritdoc />
    public NodeAddress AddressFor(string provider, string variableId) => throw NotHere();

    /// <inheritdoc />
    public void RegisterMethod(string name, MethodHandler handler) => throw NotHere();

    /// <inheritdoc />
    public void SetDiagnostic(string name, int value) => throw NotHere();

    /// <inheritdoc />
    public NodeAddress CreateQueryNodes(int queryId, SessionId session) => throw NotHere();

    /// <inheritdoc />
    public void DeleteQueryNodes(int queryId, SessionId session) => throw NotHere();

    /// <inheritdoc />
    public void WriteDiff(int queryId, SessionId session, string json) => throw NotHere();

    /// <inheritdoc />
    public ReplicationBatch Encode(IReadOnlyList<ReplicatedValue> values) => throw NotHere();

    /// <inheritdoc />
    public IReadOnlyList<ReplicatedValue> Decode(ReplicationBatch batch) => throw NotHere();
}
