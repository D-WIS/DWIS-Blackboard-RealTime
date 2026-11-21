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

    /// <summary>The namespace of the <c>DDHubServer</c> object, its methods, its diagnostics and the query nodes; found by name (decision D-56).</summary>
    public const string DictionariesNamespace = "http://ddhub.no/DDHubDictionaries";

    /// <summary>The folder that holds one folder per provider.</summary>
    public const string ProvidersFolder = "Providers";

    /// <summary>The namespace of a provider's folder: <c>http://ddhub.no/{provider}/</c>.</summary>
    public static string ProviderNamespace(string provider) => $"{LegacyRoot}{provider}/";

    /// <summary>The namespace of a provider's variables: <c>http://ddhub.no/{provider}/Variables/</c>.</summary>
    public static string VariablesNamespace(string provider) => $"{LegacyRoot}{provider}/Variables/";

    /// <summary>The node id (string part) of a provided variable: <c>{provider}.{VariableID}</c>, exactly as today.</summary>
    public static string VariableNodeId(string provider, string variableId) => $"{provider}.{variableId}";

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
