namespace DWIS.RealTime.Core.Adapters;

/// <summary>
/// The provider folders and the provided variables of the OPC UA address space (specification 3.1, decision D-54).
/// Implemented by the adapter of one OPC UA stack, called by Core. Namespaces and node ids are given by Core
/// (<see cref="Naming"/>), so that clients see the same ids whatever the stack.
/// </summary>
public interface IAddressSpace
{
    /// <summary>Creates the folder of a provider and claims its namespace <c>http://ddhub.no/{provider}/</c>. Creating it twice is harmless.</summary>
    void CreateProviderFolder(string provider);

    /// <summary>Deletes the folder of a provider with everything under it. Deleting an unknown provider is harmless.</summary>
    void DeleteProviderFolder(string provider);

    /// <summary>
    /// Creates a provided variable in the provider's folder, node id <c>{provider}.{VariableID}</c> in the namespace
    /// <c>http://ddhub.no/{provider}/Variables/</c>, and its validity boolean next to it; returns the variable's address.
    /// </summary>
    NodeAddress CreateVariable(string provider, VariableSpec spec);

    /// <summary>Deletes a provided variable and its validity boolean. Deleting an unknown variable is harmless.</summary>
    void DeleteVariable(string provider, string variableId);

    /// <summary>Writes a value with its source timestamp: the initial value at creation, and the values of the hub role. Client writes go straight to the stack.</summary>
    void WriteValue(string provider, string variableId, object? value, DateTime sourceTimestamp);

    /// <summary>The current value and source timestamp of a variable, or null when it does not exist.</summary>
    TimedValue? ReadValue(string provider, string variableId);

    /// <summary>Where a provided variable lives: what <c>CommitRequest</c> records for it (endpoint URL, namespace URI, node id).</summary>
    NodeAddress AddressFor(string provider, string variableId);
}
