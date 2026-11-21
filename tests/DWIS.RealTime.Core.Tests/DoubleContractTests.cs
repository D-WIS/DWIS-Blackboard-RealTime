using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Tests.Contract;

namespace DWIS.RealTime.Core.Tests;

/// <summary>The adapter contract against the test double (T-MOD-5).</summary>
public sealed class DoubleContractTests : AdapterContractTests
{
    private readonly TestDoubleAdapter _double = new();

    /// <inheritdoc />
    protected override (IAddressSpace AddressSpace, IMethodHost Methods, IQueryNodes QueryNodes) Adapter => (_double, _double, _double);

    /// <inheritdoc />
    protected override MethodResult Invoke(string name, SessionId session, params string[] input) => _double.Call(name, session, input);
}
