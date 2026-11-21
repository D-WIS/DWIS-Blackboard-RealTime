using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Tests;

/// <summary>Core's behaviour against the test double (T-MOD-5), milestone 4.0: the façade has its shape.</summary>
public sealed class RealTimeCoreTests
{
    private readonly TestDoubleAdapter _double = new();

    private RealTimeCore NewCore() => new(_double, _double, _double);

    [Fact]
    public async Task Start_registers_the_nine_methods_and_zeroes_the_diagnostics()
    {
        var core = NewCore();

        await core.StartAsync(TestContext.Current.CancellationToken);

        _double.Methods.Keys.Should().BeEquivalentTo(DDHubMethods.All.Select(m => m.Name));
        _double.Methods.Keys.Should().HaveCount(9);
        _double.Diagnostics.Should().Equal(new Dictionary<string, int>
        {
            [DDHubMethods.RegisteredQueryCount] = 0,
            [DDHubMethods.RegisteredQuerySubscriptionCount] = 0,
        });
    }

    [Theory]
    [InlineData(DDHubMethods.Register)]
    [InlineData(DDHubMethods.Resolve)]
    public async Task Acquisition_files_are_not_supported(string method)
    {
        var core = NewCore();
        await core.StartAsync(TestContext.Current.CancellationToken);

        var result = _double.Call(method, new SessionId("s"), "{\"AcquisitionFile\":1}");

        result.Status.Should().Be(MethodStatus.BadNotSupported);
        result.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_method_with_an_input_refuses_a_call_without_exactly_one_text()
    {
        var core = NewCore();
        await core.StartAsync(TestContext.Current.CancellationToken);

        _double.Call(DDHubMethods.Inject, new SessionId("s")).Status.Should().Be(MethodStatus.BadInvalidArgument);
        _double.Call(DDHubMethods.Inject, new SessionId("s"), "a", "b").Status.Should().Be(MethodStatus.BadInvalidArgument);
    }

    [Fact]
    public async Task Until_milestone_4_2_the_query_methods_answer_not_supported()
    {
        var core = NewCore();
        await core.StartAsync(TestContext.Current.CancellationToken);

        foreach (var name in new[] { DDHubMethods.RegisterQuery, DDHubMethods.UnregisterQuery, DDHubMethods.ResolveQuery })
        {
            _double.Call(name, new SessionId("s"), "{}").Status.Should().Be(MethodStatus.BadNotSupported, name);
        }
        _double.Call(DDHubMethods.GetResources, new SessionId("s")).Status.Should().Be(MethodStatus.BadNotSupported);
    }

    [Fact]
    public async Task Without_a_semantic_service_the_injection_methods_answer_a_communication_error()
    {
        var core = NewCore();
        await core.StartAsync(TestContext.Current.CancellationToken);

        _double.Call(DDHubMethods.Inject, new SessionId("s"), "{\"ManifestName\":\"m\",\"Provider\":{\"Name\":\"p\"}}").Status.Should().Be(MethodStatus.BadCommunicationError);
        core.Rebuilt.Should().BeFalse();
        await core.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Sessions_are_tracked_from_open_to_close_and_an_unknown_close_is_harmless()
    {
        var core = NewCore();
        var a = new SessionId("a");
        var b = new SessionId("b");

        core.SessionOpened(a);
        core.SessionOpened(b);
        core.SessionOpened(a);
        core.OpenSessions.Should().BeEquivalentTo([a, b]);

        core.SessionClosed(a);
        core.SessionClosed(new SessionId("never-opened"));
        core.OpenSessions.Should().BeEquivalentTo([b]);
    }

    [Fact]
    public void Legacy_namespace_rewriting_comes_from_the_contracts()
    {
        Naming.ToLegacy("http://d-wis.org/Acme/SPP").Should().Be("http://ddhub.no/Acme/SPP");
        Naming.LegacyRoot.Should().Be("http://ddhub.no/");
    }

    [Fact]
    public void The_declarations_are_the_old_servers()
    {
        DDHubMethods.All.Select(m => m.Name).Should().Equal("Inject", "Update", "Delete", "Register", "Resolve", "RegisterQuery", "UnregisterQuery", "ResolveQuery", "GetResources");
        DDHubMethods.All.Where(m => m.Name != DDHubMethods.GetResources).Should().AllSatisfy(m => m.Inputs.Should().ContainSingle().Which.Type.Should().Be(ArgumentType.StringArray));
        DDHubMethods.Find(DDHubMethods.RegisterQuery)!.Outputs.Should().HaveCount(2);
        DDHubMethods.Find(DDHubMethods.UnregisterQuery)!.Outputs.Should().ContainSingle().Which.Type.Should().Be(ArgumentType.Boolean);
        DDHubMethods.Find("Nope").Should().BeNull();
    }
}
