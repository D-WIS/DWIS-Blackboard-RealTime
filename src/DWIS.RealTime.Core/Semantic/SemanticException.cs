using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Semantic;

/// <summary>
/// The semantic service could not be reached, did not answer in time, or refused the call. Carries the status
/// the façade answers with (R-RT-2: fail fast with a clear status, let the client retry).
/// </summary>
public sealed class SemanticException : Exception
{
    /// <summary>Creates the exception with the status to answer.</summary>
    public SemanticException(MethodStatus status, string message, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
    }

    /// <summary>Creates the exception without a status (internal error).</summary>
    public SemanticException()
        : base("The semantic service failed.")
    {
        Status = MethodStatus.BadInternalError;
    }

    /// <summary>Creates the exception with a message and an internal-error status.</summary>
    public SemanticException(string message)
        : base(message)
    {
        Status = MethodStatus.BadInternalError;
    }

    /// <summary>Creates the exception with a message, a cause and an internal-error status.</summary>
    public SemanticException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = MethodStatus.BadInternalError;
    }

    /// <summary>The status the façade answers with.</summary>
    public MethodStatus Status { get; }
}
