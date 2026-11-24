using System.Text.Json;
using System.Text.Json.Serialization;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Queries;

/// <summary>
/// The query JSON of the old API (DWIS.API.DTO 2.x), written as Newtonsoft wrote it on the old server: the
/// <c>QueryResult</c> (<c>VariablesHeader</c>, <c>Results</c> of rows of <c>Items</c>, with the <c>Count</c> and
/// <c>IsReadOnly</c> properties the old classes expose), the <c>QueryResultsDiff</c> (<c>QueryID</c>,
/// <c>QueryResultID</c>, <c>Added</c>, <c>Removed</c>) and the <c>ResourceFile</c> (<c>DDHubIndividuals</c>,
/// <c>DDHubSentences</c>). Every IRI is rewritten to <c>http://ddhub.no/</c> on the way out (specification 3.6) and
/// split at its last <c>/</c> into a (namespace, id) pair, as the old server did; a resource with no OPC UA node is
/// the plain pair, nothing more (decision D-31).
/// </summary>
public static class QueryJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NewLine = "\n", DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>The <c>QueryResult</c> JSON of an answer.</summary>
    public static string Result(IEnumerable<string> variables, IEnumerable<Row> rows)
    {
        // The old server took the column names from its OWL query engine, which answers "?" and the variable's name in
        // upper case whatever the query says (recorded from digiwells/ddhubserver, T-BC-3): "?SIGNAL" for ?signal.
        var result = new QueryResultShape { VariablesHeader = variables.Select(v => "?" + v.ToUpperInvariant()).ToList(), Results = rows.Select(RowOf).ToList() };
        return JsonSerializer.Serialize(result, Options);
    }

    /// <summary>The <c>QueryResultsDiff</c> JSON: the query's id text, the diff variable's id text, the rows added and removed.</summary>
    public static string Diff(string queryId, string queryResultId, IEnumerable<Row> added, IEnumerable<Row> removed)
    {
        var diff = new DiffShape { QueryID = queryId, QueryResultID = queryResultId, Added = added.Select(RowOf).ToList(), Removed = removed.Select(RowOf).ToList() };
        return JsonSerializer.Serialize(diff, Options);
    }

    /// <summary>
    /// The <c>ResourceFile</c> JSON as the old server wrote it: one individual per resource with an empty
    /// <c>TypeDictionaryURI</c> (the old server never filled it, and the client does not read it), one sentence per
    /// reference between individuals with the verb's legacy IRI. The classes the service knows are left out for
    /// the same bytes (R-BC-1); they can come in with the client patch.
    /// </summary>
    public static string Resources(ResourceFile file)
    {
        var shape = new ResourceFileShape
        {
            DDHubIndividuals = file.Individuals.Select(i => i.Iri).Distinct(StringComparer.Ordinal).Select(iri => new IndividualShape(Item(iri), "")).ToList(),
            DDHubSentences = file.Sentences.Select(s => new SentenceShape(new IndividualShape(Item(s.Subject), ""), new IndividualShape(Item(s.Object), ""), Naming.ToLegacy(s.Verb))).ToList(),
        };
        return JsonSerializer.Serialize(shape, Options);
    }

    /// <summary>
    /// A row value as the old API shows it: an IRI rewritten to the legacy namespace and split at its last slash into
    /// (namespace, id); a value without a slash (a literal) keeps its text as the id with an empty namespace.
    /// </summary>
    public static NodeIdentifierShape Item(string value)
    {
        var text = Naming.ToLegacy(value);
        var slash = text.LastIndexOf('/');
        return slash >= 0 && slash < text.Length - 1 ? new NodeIdentifierShape(text[..(slash + 1)], text[(slash + 1)..]) : new NodeIdentifierShape("", text);
    }

    private static RowShape RowOf(Row row) => new() { Items = row.Values.Select(Item).ToList() };

    /// <summary>The old <c>NodeIdentifier</c>: a namespace URI and an id.</summary>
    public sealed record NodeIdentifierShape(string NameSpace, string ID);

    private sealed class RowShape
    {
        public List<NodeIdentifierShape> Items { get; set; } = [];
        public int Count => Items.Count;
        public bool IsReadOnly { get; set; }
    }

    private sealed class QueryResultShape
    {
        public List<string> VariablesHeader { get; set; } = [];
        public List<RowShape> Results { get; set; } = [];
        public int Count => Results.Count;
        public bool IsReadOnly { get; set; }
    }

    private sealed class DiffShape
    {
        public string QueryID { get; set; } = "";
        public string QueryResultID { get; set; } = "";
        public List<RowShape> Added { get; set; } = [];
        public List<RowShape> Removed { get; set; } = [];
    }

    private sealed record IndividualShape(NodeIdentifierShape NodeIdentifier, string TypeDictionaryURI);

    private sealed record SentenceShape(IndividualShape Subject, IndividualShape Object, string Verb);

    private sealed class ResourceFileShape
    {
        public List<IndividualShape> DDHubIndividuals { get; set; } = [];
        public List<SentenceShape> DDHubSentences { get; set; } = [];
    }
}
