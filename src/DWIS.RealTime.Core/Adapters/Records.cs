namespace DWIS.RealTime.Core.Adapters;

/// <summary>The id of a client session, as the stack gives it (a string, opaque to Core).</summary>
public readonly record struct SessionId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// A provided variable to create (from <c>PrepareResult.variables</c>): the id, the data type as one of the seven
/// manifest strings (<see cref="VariableDataTypes"/>), the rank and dimensions, the initial value as text (empty for
/// the type's default), and the id of its validity boolean.
/// </summary>
public sealed record VariableSpec(string VariableId, string DataType, int Rank, IReadOnlyList<int> Dimensions, string InitialValue, string ValidityVariableId);

/// <summary>The seven data type names a manifest may use for a provided variable (the old API's <c>ProvidedVariable.DataType</c>).</summary>
public static class VariableDataTypes
{
    /// <summary>The names, in the order of the old API's documentation.</summary>
    public static readonly IReadOnlyList<string> All = ["double", "float", "int", "long", "short", "string", "bool"];

    /// <summary>True for one of the seven names, whatever the case.</summary>
    public static bool IsKnown(string dataType) => All.Contains(dataType, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Where a node lives: what <c>CommitRequest.endpoints</c> records (R-RT-5) and what a client needs to find it. The
/// namespace index is the one the stack gave the namespace URI; the old <c>ManifestInjectionResult</c> carries it.
/// </summary>
public sealed record NodeAddress(string EndpointUrl, string NamespaceUri, ushort NamespaceIndex, string NodeId);

/// <summary>A value with its source timestamp.</summary>
public sealed record TimedValue(object? Value, DateTime SourceTimestamp);

/// <summary>The fixed list of statuses a method call can end with; the adapter maps them to its stack's status codes.</summary>
public enum MethodStatus
{
    /// <summary>The call succeeded; the outputs are set.</summary>
    Good,

    /// <summary>The method is not supported (<c>Register</c> and <c>Resolve</c>, R-BC-2).</summary>
    BadNotSupported,

    /// <summary>The input is not what the method takes (not JSON, not a manifest, not a query).</summary>
    BadInvalidArgument,

    /// <summary>The semantic service could not be reached or did not answer within the deadline (R-RT-2); the client may retry.</summary>
    BadCommunicationError,

    /// <summary>Something failed in the process itself; the log has the reason.</summary>
    BadInternalError,
}

/// <summary>The outcome of a method call: a status and, when good, the output values in the order of the method's declaration.</summary>
public sealed record MethodResult(MethodStatus Status, IReadOnlyList<object?> Outputs)
{
    /// <summary>A good result with these outputs.</summary>
    public static MethodResult Good(params object?[] outputs) => new(MethodStatus.Good, outputs);

    /// <summary>A failed result, no outputs.</summary>
    public static MethodResult Fail(MethodStatus status) => new(status, []);
}
