using System.Reflection;
using DWIS.RealTime.Core.Adapters;

namespace DWIS.RealTime.Core.Tests;

/// <summary>T-MOD-4: <c>DWIS.RealTime.Core</c> has no OPC UA assembly reference, so a company reuses it with any stack (R-MOD-2).</summary>
public sealed class NoOpcUaReferenceTests
{
    [Fact]
    public void Core_references_no_opc_ua_assembly()
    {
        var core = typeof(RealTimeCore).Assembly;

        var referenced = core.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        referenced.Should().NotContain(name => name.StartsWith("Opc.Ua", StringComparison.OrdinalIgnoreCase) || name.Contains("UnifiedAutomation", StringComparison.OrdinalIgnoreCase) || name.Contains("OPCFoundation", StringComparison.OrdinalIgnoreCase));
        referenced.Should().Contain(name => name.StartsWith("DWIS.Blackboard.Contracts", StringComparison.Ordinal), "Core speaks the contracts");
    }

    [Fact]
    public void The_adapter_interfaces_use_no_type_outside_core_and_the_runtime()
    {
        var interfaces = new[] { typeof(IAddressSpace), typeof(IMethodHost), typeof(IQueryNodes), typeof(ISessionEvents) };

        foreach (var type in interfaces)
        {
            foreach (var method in type.GetMethods())
            {
                var types = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
                foreach (var t in types)
                {
                    var owner = (Nullable.GetUnderlyingType(t) ?? t).Assembly;
                    (owner == typeof(RealTimeCore).Assembly || owner == typeof(object).Assembly).Should().BeTrue($"{type.Name}.{method.Name} uses {t.FullName}");
                }
            }
            type.GetMethods().Length.Should().BeGreaterThan(0);
        }
        interfaces.Sum(i => i.GetMethods().Length).Should().Be(14, "decision D-54: fourteen methods");
    }
}
