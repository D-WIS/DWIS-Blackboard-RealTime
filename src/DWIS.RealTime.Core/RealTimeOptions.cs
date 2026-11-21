namespace DWIS.RealTime.Core;

/// <summary>The settings of the real-time process (specification section 8; the <c>RealTime</c> section of the configuration layers).</summary>
public sealed class RealTimeOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "RealTime";

    /// <summary>The gRPC address of the semantic service (specification 3.3, D-50: HTTP/2 without TLS in the lab profile).</summary>
    public string SemanticAddress { get; set; } = "http://semantic:8081";

    /// <summary>The deadline of every call to the semantic service, in seconds (R-RT-2: a method fails fast with a clear status).</summary>
    public int DeadlineSeconds { get; set; } = 30;

    /// <summary>The OPC UA endpoint URL the process serves and records in <c>CommitRequest.endpoints</c>.</summary>
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:48030";

    /// <summary>How long to wait before asking the semantic service again for the list of manifests when it did not answer at start-up (R-RT-6).</summary>
    public int RetrySeconds { get; set; } = 5;

    /// <summary>The defaults as configuration keys, for the layers of <c>DWIS.Blackboard.Configuration</c>.</summary>
    public static IReadOnlyDictionary<string, string?> Defaults { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        [$"{Section}:SemanticAddress"] = "http://semantic:8081",
        [$"{Section}:DeadlineSeconds"] = "30",
        [$"{Section}:EndpointUrl"] = "opc.tcp://localhost:48030",
        [$"{Section}:RetrySeconds"] = "5",
    };
}
