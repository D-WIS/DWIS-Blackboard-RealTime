using System.Text.Json;
using System.Text.Json.Serialization;
using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Injection;

/// <summary>
/// The <c>ManifestInjectionResult</c> JSON of the old API (DWIS.API.DTO 2.x), written with the same property names,
/// order and nulls as the old server (R-BC-1): <c>Success</c>; the injected-variable namespace and list, null since
/// the Rig Edition drops injected variables (R-BC-3); the nodes namespace (the provider's) with an empty list, since
/// no node is created for them (R-RT-3, D-21); the variables namespace and one mapping per provided variable with
/// the node id and namespace index the client writes to.
/// </summary>
public static class InjectionResultJson
{
    // Indented with two spaces and LF line ends, as Newtonsoft wrote it on the old server's Linux host, whatever this host is.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NewLine = "\n", DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>The JSON for a manifest that went live, with the addresses of its provided variables in manifest order.</summary>
    public static string Success(string provider, IReadOnlyList<(string VariableId, NodeAddress Address)> variables)
    {
        var result = new Result
        {
            Success = true,
            InjectedNodesNamespace = Naming.ProviderNamespace(provider),
            ProvidedVariablesNamespace = Naming.VariablesNamespace(provider),
            InjectedNodes = [],
            ProvidedVariables = variables.Select(v => new Mapping(v.VariableId, new NodeId(v.Address.NamespaceIndex, v.Address.NodeId))).ToList(),
        };
        return JsonSerializer.Serialize(result, Options);
    }

    /// <summary>The JSON the old server answers when an injection fails: <c>Success</c> false and nothing else.</summary>
    public static string Failure() => JsonSerializer.Serialize(new Result { Success = false }, Options);

    private sealed class Result
    {
        public bool Success { get; set; }
        public string? InjectedVariableNamespace { get; set; }
        public string? InjectedNodesNamespace { get; set; }
        public string? ProvidedVariablesNamespace { get; set; }
        public List<Mapping>? InjectedVariables { get; set; }
        public List<Mapping>? InjectedNodes { get; set; }
        public List<Mapping>? ProvidedVariables { get; set; }
    }

    private sealed record Mapping(string ManifestItemID, NodeId InjectedID);

    private sealed record NodeId(ushort NameSpaceIndex, string ID);
}
