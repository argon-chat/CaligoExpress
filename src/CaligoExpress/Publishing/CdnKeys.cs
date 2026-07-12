namespace CaligoExpress.Publishing;

/// <summary>
/// The canonical key (mirror-relative path) scheme shared by the publisher and the client, rooted at an
/// app prefix. The DEFAULT app (Argon) keeps the historical ROOT layout — shipped bootstrappers hard-code
/// those paths — while every other app gets the identical scheme under <c>apps/&lt;appId&gt;/</c>, so
/// multiple apps can share the same hosts, buckets and worker. Mirrors src/CaligoExpress.Cloud/src/keys.ts.
/// </summary>
public sealed class CdnKeyScheme
{
    /// <summary>The legacy root layout (the default app's — and every pre-multi-app deployment's — scheme).</summary>
    public static readonly CdnKeyScheme Root = new(string.Empty);

    /// <summary>Scheme for an app: null/empty appId → the legacy root layout, else <c>apps/&lt;appId&gt;/</c>.</summary>
    public static CdnKeyScheme ForApp(string? appId) =>
        string.IsNullOrEmpty(appId) ? Root : new CdnKeyScheme($"apps/{appId}/");

    private CdnKeyScheme(string prefix) => Prefix = prefix;

    /// <summary>"" for the root layout, or <c>apps/&lt;appId&gt;/</c>.</summary>
    public string Prefix { get; }

    public string Config => $"{Prefix}config.json";

    public string Release(string channel) => $"{Prefix}{channel}/release.json";

    /// <summary>The bootstrapper's own signed pointer for a channel, separate from the app release.</summary>
    public string BootstrapperManifest(string channel) => $"{Prefix}{channel}/bootstrapper.json";

    public string PackageManifest(string name, string version) => $"{Prefix}{name}/{version}/package.json";

    /// <summary>Content-addressed chunk object, sharded by the first hash byte to keep listings shallow.
    /// The hash <em>is</em> the address, so identical chunks across files/versions collapse to one object.</summary>
    public string Chunk(string hash) => $"{Prefix}chunks/{hash[..2]}/{hash}";

    public string Bootstrapper(string version) => $"{Prefix}bootstrapper/{version}/bootstrapper.bin";

    /// <summary>The macOS bootstrapper binary — a separate key from the Windows one (different artifact: a
    /// <c>.app</c> bundle ditto-zip), so the two never collide at the same version.</summary>
    public string BootstrapperMac(string version) => $"{Prefix}bootstrapper/{version}/bootstrapper.mac.zip";

    /// <summary>The macOS whole-app bundle archive for a version (one content-addressed <c>ditto</c> zip).</summary>
    public string MacApp(string version) => $"{Prefix}macapp/{version}/app.zip";
}

/// <summary>The legacy root-layout key scheme (static shorthand for <see cref="CdnKeyScheme.Root"/>).</summary>
public static class CdnKeys
{
    public const string Config = "config.json";

    public static string Release(string channel) => CdnKeyScheme.Root.Release(channel);

    /// <summary>The bootstrapper's own signed pointer for a channel, separate from the app release.</summary>
    public static string BootstrapperManifest(string channel) => CdnKeyScheme.Root.BootstrapperManifest(channel);

    public static string PackageManifest(string name, string version) => CdnKeyScheme.Root.PackageManifest(name, version);

    public static string Chunk(string hash) => CdnKeyScheme.Root.Chunk(hash);

    /// <summary>True for keys produced by <see cref="Chunk"/> — used by the worker's GC to scope pruning.</summary>
    public static bool IsChunk(string key) => key.StartsWith("chunks/", StringComparison.Ordinal);

    public static string Bootstrapper(string version) => CdnKeyScheme.Root.Bootstrapper(version);

    public static string BootstrapperMac(string version) => CdnKeyScheme.Root.BootstrapperMac(version);

    public static string MacApp(string version) => CdnKeyScheme.Root.MacApp(version);
}
