namespace DWIS.RealTime.Core.Hub;

/// <summary>The settings of the hub role (specification section 6; the <c>RealTime:Hub</c> section). Off while the URL is empty.</summary>
public sealed class HubOptions
{
    /// <summary>The hub's base URL (the old hub serves <c>/dwishub</c> under it); empty means no hub role.</summary>
    public string Url { get; set; } = "";

    /// <summary>The group this Blackboard replicates with; the old hub's default group.</summary>
    public string Group { get; set; } = "default";

    /// <summary>The name of this site, for the logs and the origin of what this process sends.</summary>
    public string Site { get; set; } = "";

    /// <summary>The password of the group (the old hub's protected groups); empty for an open group.</summary>
    public string Password { get; set; } = "";

    /// <summary>How often the buffered values and manifests go out, in milliseconds (the old client used 200).</summary>
    public int BatchMilliseconds { get; set; } = 200;

    /// <summary>True when a hub URL is set.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Url);
}
