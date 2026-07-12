namespace CaligoExpress.Publishing;

using CaligoExpress.Manifests;

/// <summary>Derives the two generated artifacts from the central <see cref="CaligoProjectConfig"/>.</summary>
public static class ConfigBuilder
{
    /// <summary>The bootstrap document served to clients (unsigned here; sign with <see cref="Crypto.ManifestTrust"/>).</summary>
    public static ConfigManifest BuildConfig(CaligoProjectConfig project)
    {
        var scheme = CdnKeyScheme.ForApp(project.AppId);
        var channels = project.Channels.ToDictionary(c => c, scheme.Release);
        var keys = string.IsNullOrEmpty(project.PublicKey)
            ? new List<PublicKeyEntry>()
            : [new PublicKeyEntry(project.KeyId, "ed25519", project.PublicKey)];

        return new ConfigManifest(
            SchemaVersion: 1,
            Mirrors: project.Mirrors,
            Channels: channels,
            PublicKeys: keys,
            MinBootstrapper: project.MinBootstrapper);
    }

    /// <summary>The config slice embedded in the installer.</summary>
    public static InstallerRuntimeConfig BuildInstallerRuntime(CaligoProjectConfig project)
    {
        var pinned = string.IsNullOrEmpty(project.PublicKey)
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [project.KeyId] = project.PublicKey };

        return new InstallerRuntimeConfig(
            project.SeedHosts, pinned, project.BootstrapperFileName, project.DefaultChannel, project.Scheme,
            string.IsNullOrEmpty(project.AppId) ? null : project.AppId);
    }
}
