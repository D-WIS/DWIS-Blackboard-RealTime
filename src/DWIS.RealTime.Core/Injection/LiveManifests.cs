namespace DWIS.RealTime.Core.Injection;

/// <summary>A manifest live in the semantic service and the nodes this process created for it.</summary>
public sealed record LiveManifest(ManifestKey Key, string ContentHash, IReadOnlyList<string> VariableIds);

/// <summary>
/// The local table from manifest id to the nodes it created (specification 3.1): rebuilt from the service at
/// start-up, kept current by the façade, never persisted (R-RT-6, R-SEM-7).
/// </summary>
public sealed class LiveManifests
{
    private readonly Dictionary<ManifestKey, LiveManifest> _manifests = [];
    private readonly Lock _gate = new();

    /// <summary>The manifest with this key, or null.</summary>
    public LiveManifest? Get(ManifestKey key)
    {
        lock (_gate) { return _manifests.GetValueOrDefault(key); }
    }

    /// <summary>Records a manifest as live with its variables.</summary>
    public void Set(LiveManifest manifest)
    {
        lock (_gate) { _manifests[manifest.Key] = manifest; }
    }

    /// <summary>Forgets a manifest; returns what was recorded, or null.</summary>
    public LiveManifest? Remove(ManifestKey key)
    {
        lock (_gate) { return _manifests.Remove(key, out var manifest) ? manifest : null; }
    }

    /// <summary>True when another live manifest of the provider exists (so its folder stays).</summary>
    public bool ProviderHasOthers(string provider, ManifestKey except)
    {
        lock (_gate) { return _manifests.Keys.Any(k => k.Provider == provider && k != except); }
    }

    /// <summary>The live manifests, a copy.</summary>
    public IReadOnlyList<LiveManifest> All
    {
        get { lock (_gate) { return [.. _manifests.Values]; } }
    }

    /// <summary>How many manifests are live.</summary>
    public int Count
    {
        get { lock (_gate) { return _manifests.Count; } }
    }
}
