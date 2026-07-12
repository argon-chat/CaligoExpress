namespace CaligoExpress.Manifests;

using System.Text.Json.Serialization;

/// <summary>What is currently installed for one package. <see cref="ManifestHash"/> is the SHA-256 of
/// the package.json that was applied; <see cref="Manifest"/> embeds that listing so the engine can
/// compute removals and repair the tree without re-fetching anything.</summary>
public sealed record InstalledPackage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("manifestHash")] string ManifestHash,
    [property: JsonPropertyName("manifest")] PackageManifest? Manifest = null);

/// <summary>
/// Local source of truth for the install (persisted as state.json, written atomically with a .bak).
/// Records the installed release pointer and per-package versions so the engine can diff against
/// a target release without re-hashing the whole tree.
/// </summary>
public sealed class LocalInstallState
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("channel")] public string Channel { get; set; } = "live";
    [JsonPropertyName("releaseSeq")] public long ReleaseSeq { get; set; }
    [JsonPropertyName("releaseId")] public string ReleaseId { get; set; } = "";
    [JsonPropertyName("bootstrapperVersion")] public string BootstrapperVersion { get; set; } = "";
    [JsonPropertyName("packages")] public Dictionary<string, InstalledPackage> Packages { get; set; } = new();
}
