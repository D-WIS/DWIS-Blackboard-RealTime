namespace DWIS.RealTime.Core.Adapters;

/// <summary>
/// The <c>DDHubServer</c> object with its method nodes and diagnostic variables (specification 3.1, decision D-54).
/// Implemented by the adapter, called by Core. The adapter owns the fixed facts of the object: its namespace
/// <see cref="Naming.ServerNamespace"/>, the method names and argument declarations of <see cref="DDHubMethods"/>.
/// </summary>
public interface IMethodHost
{
    /// <summary>
    /// Registers the handler of one of the nine methods (<see cref="DDHubMethods.All"/>). The adapter calls the handler
    /// with the calling session and the string array input of the method, and answers the call with the handler's
    /// status and outputs. Registering a name twice replaces the handler.
    /// </summary>
    void RegisterMethod(string name, MethodHandler handler);

    /// <summary>Sets one of the diagnostic variables (<see cref="DDHubMethods.RegisteredQueryCount"/>, <see cref="DDHubMethods.RegisteredQuerySubscriptionCount"/>).</summary>
    void SetDiagnostic(string name, int value);
}

/// <summary>Handles one call of a <c>DDHubServer</c> method: the calling session and the string array input, as the clients send it.</summary>
public delegate MethodResult MethodHandler(SessionId session, string[] input);
