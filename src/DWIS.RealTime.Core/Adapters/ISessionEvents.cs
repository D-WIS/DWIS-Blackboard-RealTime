namespace DWIS.RealTime.Core.Adapters;

/// <summary>
/// What Core wants to hear from the stack about sessions (decision D-54). Implemented by Core, called by the adapter
/// from the stack's own session hooks. On close, Core releases the session's registered queries and their nodes.
/// </summary>
public interface ISessionEvents
{
    /// <summary>A client session was opened.</summary>
    void SessionOpened(SessionId session);

    /// <summary>A client session is closing (by the client, or by the server on timeout).</summary>
    void SessionClosed(SessionId session);
}
