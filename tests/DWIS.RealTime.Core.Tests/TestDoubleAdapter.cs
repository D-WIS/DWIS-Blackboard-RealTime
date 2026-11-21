using System.Globalization;
using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Tests;

/// <summary>
/// The test double of the adapter (R-MOD-2, T-MOD-5): the three interfaces over dictionaries, with what a test needs
/// to look at afterwards. Core's behaviour tests run against it; the contract tests run against it and against the
/// OPC Foundation adapter alike.
/// </summary>
public sealed class TestDoubleAdapter : IAddressSpace, IMethodHost, IQueryNodes
{
    /// <summary>The endpoint URL the double reports in every address.</summary>
    public const string EndpointUrl = "opc.tcp://double:4840";

    private readonly Lock _gate = new();

    /// <summary>The providers with a folder, in creation order.</summary>
    public List<string> Providers { get; } = [];

    /// <summary>The variables by (provider, id): their spec, value and timestamp.</summary>
    public Dictionary<(string Provider, string VariableId), VariableState> Variables { get; } = [];

    /// <summary>The registered method handlers by name.</summary>
    public Dictionary<string, MethodHandler> Methods { get; } = [];

    /// <summary>The diagnostics by name.</summary>
    public Dictionary<string, int> Diagnostics { get; } = [];

    /// <summary>The query nodes by (query id, session) and the last diff written to each.</summary>
    public Dictionary<(int QueryId, SessionId Session), string?> QueryNodes { get; } = [];

    /// <summary>Calls a registered method as a client would.</summary>
    public MethodResult Call(string name, SessionId session, params string[] input)
    {
        MethodHandler handler;
        lock (_gate)
        {
            if (!Methods.TryGetValue(name, out handler!))
            {
                throw new InvalidOperationException($"No handler registered for {name}.");
            }
        }
        return handler(session, input);
    }

    /// <inheritdoc />
    public void CreateProviderFolder(string provider)
    {
        lock (_gate)
        {
            if (!Providers.Contains(provider))
            {
                Providers.Add(provider);
            }
        }
    }

    /// <inheritdoc />
    public void DeleteProviderFolder(string provider)
    {
        lock (_gate)
        {
            Providers.Remove(provider);
            foreach (var key in Variables.Keys.Where(k => k.Provider == provider).ToList())
            {
                Variables.Remove(key);
            }
        }
    }

    /// <inheritdoc />
    public NodeAddress CreateVariable(string provider, VariableSpec spec)
    {
        lock (_gate)
        {
            if (!Providers.Contains(provider))
            {
                throw new InvalidOperationException($"Provider {provider} has no folder.");
            }
            Variables[(provider, spec.VariableId)] = new VariableState(spec, DefaultValue(spec), DateTime.UtcNow);
            return AddressFor(provider, spec.VariableId);
        }
    }

    /// <inheritdoc />
    public void DeleteVariable(string provider, string variableId)
    {
        lock (_gate) { Variables.Remove((provider, variableId)); }
    }

    /// <inheritdoc />
    public void WriteValue(string provider, string variableId, object? value, DateTime sourceTimestamp)
    {
        lock (_gate)
        {
            if (Variables.TryGetValue((provider, variableId), out var state))
            {
                Variables[(provider, variableId)] = state with { Value = value, SourceTimestamp = sourceTimestamp };
            }
        }
    }

    /// <inheritdoc />
    public TimedValue? ReadValue(string provider, string variableId)
    {
        lock (_gate)
        {
            return Variables.TryGetValue((provider, variableId), out var state) ? new TimedValue(state.Value, state.SourceTimestamp) : null;
        }
    }

    /// <inheritdoc />
    public NodeAddress AddressFor(string provider, string variableId)
    {
        lock (_gate)
        {
            var order = Providers.IndexOf(provider);
            return new NodeAddress(EndpointUrl, Naming.VariablesNamespace(provider), (ushort)(order < 0 ? 0 : 3 + order), Naming.VariableNodeId(provider, variableId));
        }
    }

    /// <inheritdoc />
    public void RegisterMethod(string name, MethodHandler handler)
    {
        lock (_gate) { Methods[name] = handler; }
    }

    /// <inheritdoc />
    public void SetDiagnostic(string name, int value)
    {
        lock (_gate) { Diagnostics[name] = value; }
    }

    /// <inheritdoc />
    public NodeAddress CreateQueryNodes(int queryId, SessionId session)
    {
        lock (_gate) { QueryNodes.TryAdd((queryId, session), null); }
        return new NodeAddress(EndpointUrl, Naming.DictionariesNamespace, 2, Naming.QueryDiffNodeId(queryId, session));
    }

    /// <inheritdoc />
    public void DeleteQueryNodes(int queryId, SessionId session)
    {
        lock (_gate) { QueryNodes.Remove((queryId, session)); }
    }

    /// <inheritdoc />
    public void WriteDiff(int queryId, SessionId session, string json)
    {
        lock (_gate)
        {
            if (QueryNodes.ContainsKey((queryId, session)))
            {
                QueryNodes[(queryId, session)] = json;
            }
        }
    }

    /// <summary>The initial value of a spec: the text parsed as the data type, or the type's default; arrays stay null.</summary>
    public static object? DefaultValue(VariableSpec spec)
    {
        if (spec.Rank > 0)
        {
            return null;
        }
        var text = spec.InitialValue;
        var hasText = !string.IsNullOrEmpty(text);
        return spec.DataType.ToUpperInvariant() switch
        {
            "DOUBLE" => hasText ? double.Parse(text, CultureInfo.InvariantCulture) : 0.0,
            "FLOAT" => hasText ? float.Parse(text, CultureInfo.InvariantCulture) : 0.0f,
            "INT" => hasText ? int.Parse(text, CultureInfo.InvariantCulture) : 0,
            "LONG" => hasText ? long.Parse(text, CultureInfo.InvariantCulture) : 0L,
            "SHORT" => hasText ? short.Parse(text, CultureInfo.InvariantCulture) : (short)0,
            "BOOL" => hasText && bool.Parse(text),
            _ => text,
        };
    }

    /// <summary>A variable of the double.</summary>
    public sealed record VariableState(VariableSpec Spec, object? Value, DateTime SourceTimestamp);
}
