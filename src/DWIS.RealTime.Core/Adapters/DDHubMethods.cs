namespace DWIS.RealTime.Core.Adapters;

/// <summary>One argument of a <c>DDHubServer</c> method: a one-dimensional string array in, a scalar out, as the old server declares them.</summary>
/// <param name="Name">The argument's name.</param>
/// <param name="Type">The scalar type: <see cref="ArgumentType.StringArray"/> for inputs, <see cref="ArgumentType.Text"/> or <see cref="ArgumentType.Boolean"/> for outputs.</param>
/// <param name="Description">The description shown to clients.</param>
public sealed record MethodArgument(string Name, ArgumentType Type, string Description);

/// <summary>The three argument types the façade uses.</summary>
public enum ArgumentType
{
    /// <summary>A one-dimensional string array (every input: the clients wrap their one JSON or SPARQL text in a one-element array).</summary>
    StringArray,

    /// <summary>A scalar string.</summary>
    Text,

    /// <summary>A scalar boolean.</summary>
    Boolean,
}

/// <summary>The declaration of one method: its name (the node id and browse name), display name, inputs and outputs.</summary>
public sealed record MethodDeclaration(string Name, string DisplayName, IReadOnlyList<MethodArgument> Inputs, IReadOnlyList<MethodArgument> Outputs);

/// <summary>
/// The nine methods of the <c>DDHubServer</c> object and its two diagnostic variables, declared exactly as the old
/// server declares them (<c>DWIS-Blackboard-Server/src/DWIS.OPCUA.ServerManager.OpcFoundation/DWISNodeManager.cs:306-363</c>),
/// so that the C# client 2.2.x calls them unchanged (R-BC-1). The adapter creates the nodes from this list; Core
/// registers a handler per name.
/// </summary>
public static class DDHubMethods
{
    private static MethodArgument In(string name, string description) => new(name, ArgumentType.StringArray, description);
    private static MethodArgument Out(string name, string description) => new(name, ArgumentType.Text, description);

    /// <summary>The object node under <c>Objects</c>.</summary>
    public const string ObjectName = "DDHubServer";

    /// <summary>Inject a manifest; answers the <c>ManifestInjectionResult</c> JSON.</summary>
    public const string Inject = "Inject";

    /// <summary>Update a manifest (remove plus inject under the same id); answers the <c>ManifestInjectionResult</c> JSON.</summary>
    public const string Update = "Update";

    /// <summary>Delete a manifest; answers whether it was live.</summary>
    public const string Delete = "Delete";

    /// <summary>Register an acquisition file: not supported (R-BC-2).</summary>
    public const string Register = "Register";

    /// <summary>Resolve an acquisition file: not supported (R-BC-2).</summary>
    public const string Resolve = "Resolve";

    /// <summary>Register a SPARQL query for the calling session; answers the initial diff JSON and the query id.</summary>
    public const string RegisterQuery = "RegisterQuery";

    /// <summary>Unregister a query of the calling session; answers whether the session owned it.</summary>
    public const string UnregisterQuery = "UnregisterQuery";

    /// <summary>Answer a SPARQL query once; answers the <c>QueryResult</c> JSON.</summary>
    public const string ResolveQuery = "ResolveQuery";

    /// <summary>The individuals and sentences of the live description; answers the resource file JSON.</summary>
    public const string GetResources = "GetResources";

    /// <summary>Diagnostic variable: the distinct registered queries.</summary>
    public const string RegisteredQueryCount = "RegisteredQueryCount";

    /// <summary>Diagnostic variable: the query nodes over every session.</summary>
    public const string RegisteredQuerySubscriptionCount = "RegisteredQuerySubscriptionCount";

    /// <summary>The nine declarations, in the order the old server creates the nodes.</summary>
    public static IReadOnlyList<MethodDeclaration> All { get; } =
    [
        new(Inject, "Inject Manifest",
            [In("ManifestFile", "JSon serialized manifest file.")],
            [Out("InjectionResult", "The details of the injection process")]),
        new(Update, "Update Manifest",
            [In("ManifestFile", "JSon serialized manifest file.")],
            [Out("InjectionResult", "The details of the injection process")]),
        new(Delete, "Delete Manifest",
            [In("ManifestFile", "JSon serialized manifest file.")],
            [new MethodArgument("Removed", ArgumentType.Boolean, "Whether the manifest was live and is now removed.")]),
        new(Register, "Register Acquisition File",
            [In("AcquisitionFile", "JSon serialized acquisition file.")],
            [Out("AcquisitionFileID", "The ID associated to the acquisition file")]),
        new(Resolve, "Resolve Acquisition File",
            [In("AcquisitionFile", "JSon serialized acquisition file.")],
            [Out("ResolutionResult", "The results of the resolution process")]),
        new(RegisterQuery, "Register Query",
            [In("Query", "SparQL query.")],
            [Out("Initial query diff", "The initial result of the query resolution, as a json query diff"), Out("QueryID", "The ID associated to the query")]),
        new(UnregisterQuery, "Unregister Query",
            [In("Query", "SparQL query owned by this session.")],
            [new MethodArgument("Released", ArgumentType.Boolean, "Whether this session owned the query.")]),
        new(ResolveQuery, "Resolve Query",
            [In("Query", "SparQL query.")],
            [Out("QueryResolution", "The results of the query resolution process")]),
        new(GetResources, "Get resources",
            [],
            [Out("Resources", "The resources stored by the server, as a json resource file")]),
    ];

    /// <summary>The two diagnostic variables (Int32, read-only) with their descriptions.</summary>
    public static IReadOnlyList<(string Name, string Description)> Diagnostics { get; } =
    [
        (RegisteredQueryCount, "Distinct active SPARQL queries"),
        (RegisteredQuerySubscriptionCount, "Session query notification nodes"),
    ];

    /// <summary>The declaration of a method by name, or null.</summary>
    public static MethodDeclaration? Find(string name) => All.FirstOrDefault(m => m.Name == name);
}
