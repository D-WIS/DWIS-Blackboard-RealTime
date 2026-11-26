namespace DWIS.RealTime.Core.Adapters;

/// <summary>
/// What the adapter tells Core about client writes (decision D-63, the hub role): implemented by Core, called by the
/// adapter on its own thread when a client writes a provided variable. A value Core writes itself through
/// <see cref="IAddressSpace.WriteValue"/> (the initial value, a peer's value from the hub) raises nothing, so a
/// peer's value is never echoed back to the hub. Nothing of the value path waits on it (R-RT-1): Core only buffers.
/// </summary>
public interface IValueEvents
{
    /// <summary>A client wrote a provided variable (not its validity boolean).</summary>
    void ValueWritten(string provider, string variableId, object? value, DateTime sourceTimestamp);
}
