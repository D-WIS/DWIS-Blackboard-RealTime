using System.Security.Cryptography;
using System.Text;

namespace DWIS.RealTime.Core.Adapters;

/// <summary>
/// The names clients depend on (specification 3.1 and 3.6, R-BC-1), given by Core so that every adapter produces the same:
/// the namespace of the <c>DDHubServer</c> object, the namespaces and node ids of providers and provided variables
/// (as the old server's <c>DWIS.Vocabulary.ResourceIntegration.ResourceManager</c> forms them), and the query node names.
/// On the OPC UA side the legacy namespace <c>http://ddhub.no/</c> stays (3.6); the semantic service translates.
/// </summary>
public static class Naming
{
    /// <summary>The legacy root that OPC UA clients compare namespace URIs against.</summary>
    public const string LegacyRoot = DWIS.Blackboard.Contracts.Namespaces.Legacy;

    /// <summary>The namespace of the <c>DDHubServer</c> object, its methods, its diagnostics and the query nodes (decision D-58). Clients find the object by browse name, not by this name (D-57).</summary>
    public const string ServerNamespace = "http://ddhub.no/DDHubServer";

    /// <summary>The folder that holds one folder per provider.</summary>
    public const string ProvidersFolder = "Providers";

    /// <summary>The namespace of a provider's folder: <c>http://ddhub.no/{provider}/</c>, the name made safe (<see cref="Safe"/>).</summary>
    public static string ProviderNamespace(string provider) => $"{LegacyRoot}{Safe(provider)}/";

    /// <summary>The namespace of a provider's variables: <c>http://ddhub.no/{provider}/Variables/</c>, the name made safe (<see cref="Safe"/>).</summary>
    public static string VariablesNamespace(string provider) => $"{LegacyRoot}{Safe(provider)}/Variables/";

    /// <summary>The node id (string part) of a provided variable: <c>{provider}.{VariableID}</c>, both made safe (<see cref="Safe"/>), otherwise exactly as today.</summary>
    public static string VariableNodeId(string provider, string variableId) => $"{Safe(provider)}.{Safe(variableId)}";

    /// <summary>
    /// A name made safe for a namespace URI and a node id (decision D-62, <see cref="DWIS.Blackboard.Contracts.Names.IriSafe"/>):
    /// a blank or another character an IRI cannot carry becomes an underscore, the same rule the semantic service applies
    /// to its IRIs, so that a query result names the namespace and node id this process claimed. Browse names stay as written.
    /// </summary>
    public static string Safe(string name) => DWIS.Blackboard.Contracts.Names.IriSafe(name);

    /// <summary>The provider and variable id behind a variables namespace URI and a node id, as this process names them (the hub role, D-63); null when they are not a provided variable's.</summary>
    public static (string Provider, string VariableId)? VariableOf(string namespaceUri, string nodeId)
    {
        const string suffix = "/Variables/";
        if (string.IsNullOrEmpty(namespaceUri) || !namespaceUri.StartsWith(LegacyRoot, StringComparison.Ordinal) || !namespaceUri.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }
        var provider = namespaceUri[LegacyRoot.Length..^suffix.Length];
        var prefix = provider + ".";
        return provider.Length > 0 && nodeId is not null && nodeId.StartsWith(prefix, StringComparison.Ordinal) && nodeId.Length > prefix.Length
            ? (provider, nodeId[prefix.Length..])
            : null;
    }

    /// <summary>An IRI as the OPC UA side shows it: the canonical namespace rewritten back to the legacy one (specification 3.6, R-BC-1).</summary>
    public static string ToLegacy(string iri) => DWIS.Blackboard.Contracts.Namespaces.ToLegacy(iri);

    /// <summary>The suffix of a validity boolean's id (R-RT-4).</summary>
    public const string ValiditySuffix = "_validitySignal";

    /// <summary>The query nodes' suffix for a session: an underscore and the SHA-256 of the session id in hex, as the old server forms it.</summary>
    public static string SessionSuffix(SessionId session) => "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.Value)));

    /// <summary>The node id (string part) of the <c>QueryClient</c> object of a query for a session.</summary>
    public static string QueryClientNodeId(int queryId, SessionId session) => "QueryClient" + queryId + SessionSuffix(session);

    /// <summary>The node id (string part) of the <c>QueryDiff</c> variable of a query for a session.</summary>
    public static string QueryDiffNodeId(int queryId, SessionId session) => "QueryDiff" + queryId + SessionSuffix(session);
}
