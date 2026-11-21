using System.Text;
using System.Text.Json;
using DWIS.Blackboard.Contracts.Grpc;
using DWIS.Blackboard.Contracts.Manifests;

namespace DWIS.RealTime.Core.Injection;

/// <summary>
/// The id of a manifest as the semantic service forms it (Appendix C.5): the provider name as written, and the
/// manifest name made safe for a path (lower case, runs of anything but letters and digits replaced by one hyphen).
/// The same rule as the service's <c>ManifestId</c>, so that the local table and <c>ListManifests</c> agree.
/// </summary>
public sealed record ManifestKey(string Provider, string Name)
{
    /// <summary>The id, <c>{provider}/{name}</c>.</summary>
    public string Value => $"{Provider}/{Name}";

    /// <summary>The key of a parsed manifest; null when it has no provider name or no manifest name.</summary>
    public static ManifestKey? Of(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.Provider.Name) || string.IsNullOrWhiteSpace(manifest.ManifestName))
        {
            return null;
        }
        var name = Slug(manifest.ManifestName);
        return name.Length == 0 ? null : new ManifestKey(manifest.Provider.Name, name);
    }

    /// <summary>The key of a manifest reference of the contract (the service already made the name safe).</summary>
    public static ManifestKey Of(ManifestRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new ManifestKey(reference.Provider, Slug(reference.Name));
    }

    /// <summary>Parses the manifest JSON and gives its key; null when the text is not a manifest or has no names.</summary>
    public static (Manifest? Manifest, ManifestKey? Key) Parse(string json)
    {
        try
        {
            var manifest = ManifestJson.Parse(json);
            return (manifest, Of(manifest));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>The reference of the contract.</summary>
    public ManifestRef ToRef() => new() { Provider = Provider, Name = Name };

    /// <summary>The name made safe for a path.</summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        var pendingHyphen = false;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }
                pendingHyphen = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingHyphen = true;
            }
        }
        return builder.ToString();
    }
}
