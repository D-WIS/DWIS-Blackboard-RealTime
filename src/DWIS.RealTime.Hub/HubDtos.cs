using DWIS.Blackboard.Contracts.Manifests;

namespace DWIS.RealTime.Hub;

/// <summary>
/// The messages of the old hub (<c>DWIS.SignalR.DTO</c>), property for property, since the hub serialises them with
/// MessagePack by property name and the peers on the other stack read them as they are (R-HUB-1, R-HUB-2).
/// </summary>
public sealed class RealTimeDataDTO
{
    /// <summary>Who sent the batch.</summary>
    public Guid Sender { get; set; }

    /// <summary>The node id array in OPC UA binary.</summary>
    public byte[]? NodeIDs { get; set; }

    /// <summary>One attribute id per value (13 is Value).</summary>
    public uint[]? Attributes { get; set; }

    /// <summary>The data value array in OPC UA binary.</summary>
    public byte[]? Values { get; set; }

    /// <summary>The sender's namespace table, index to URI.</summary>
    public string[]? Namespaces { get; set; }
}

/// <summary>Manifests on the hub: the sender and the manifests as the old API shaped them (the Contracts type has the same properties).</summary>
public sealed class ManifestFileDTO
{
    /// <summary>Who sent them.</summary>
    public Guid Sender { get; set; }

    /// <summary>The manifests.</summary>
    public Manifest[]? ManifestFiles { get; set; }
}

/// <summary>What the hub kept for a group's session.</summary>
public sealed class SessionManifestDTO
{
    /// <summary>The manifest messages, in the order they were sent.</summary>
    public ManifestFileDTO[]? Manifests { get; set; }
}

/// <summary>The names of the old hub's route, methods and messages (<c>DWIS.SignalR.DTO.Constants</c>).</summary>
public static class HubNames
{
    /// <summary>The hub's route under its base URL.</summary>
    public const string Route = "/dwishub";

    /// <summary>The message and the method of a batch of values.</summary>
    public const string SendSignals = "SendSignals";

    /// <summary>The message and the method of manifests.</summary>
    public const string SendManifest = "SendManifest";

    /// <summary>The method that answers the manifests kept for the session.</summary>
    public const string GetSessionManifests = "GetSessionManifests";

    /// <summary>The message with the group's connection count.</summary>
    public const string ConnectionInfo = "ConnectionInfo";

    /// <summary>The method that registers a member to a group with its password; answers the write rights.</summary>
    public const string RegisterToGroup = "RegisterToGroup";

    /// <summary>The method that answers the group's connection count.</summary>
    public const string GetConnectionCount = "GetConnectionCount";
}
