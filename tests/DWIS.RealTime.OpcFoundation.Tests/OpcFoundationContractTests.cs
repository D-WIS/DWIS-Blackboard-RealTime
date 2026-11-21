using DWIS.RealTime.Core.Adapters;
using DWIS.RealTime.Tests.Contract;

namespace DWIS.RealTime.OpcFoundation.Tests;

/// <summary>The adapter contract against the OPC Foundation adapter on an in-process server (T-MOD-5).</summary>
public sealed class OpcFoundationContractTests : AdapterContractTests, IClassFixture<ServerFixture>
{
    private readonly ServerFixture _server;

    public OpcFoundationContractTests(ServerFixture server)
    {
        _server = server;
    }

    /// <inheritdoc />
    protected override (IAddressSpace AddressSpace, IMethodHost Methods, IQueryNodes QueryNodes) Adapter =>
        (_server.NodeManager, _server.NodeManager, _server.NodeManager);

    /// <inheritdoc />
    protected override MethodResult Invoke(string name, SessionId session, params string[] input) => _server.NodeManager.Dispatch(name, session, input);
}
