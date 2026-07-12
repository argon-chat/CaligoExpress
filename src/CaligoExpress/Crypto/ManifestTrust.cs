namespace CaligoExpress.Crypto;

using System.Text;
using CaligoExpress.Abstractions;
using CaligoExpress.Manifests;

/// <summary>
/// Typed signing/verification for the two signed manifest kinds. The trust chain: a key pinned in the
/// binary verifies <see cref="ConfigManifest"/>; the config's published keys then verify the
/// <see cref="ReleaseManifest"/>; the release's artifact hashes cover everything below it.
/// </summary>
public static class ManifestTrust
{
    public static ReleaseManifest Sign(ReleaseManifest release, string keyId, byte[] privateKey)
    {
        var unsigned = CaligoJson.Serialize(release with { Signature = null });
        return release with { Signature = ManifestSigning.Sign(unsigned, keyId, privateKey) };
    }

    public static ConfigManifest Sign(ConfigManifest config, string keyId, byte[] privateKey)
    {
        var unsigned = CaligoJson.Serialize(config with { Signature = null });
        return config with { Signature = ManifestSigning.Sign(unsigned, keyId, privateKey) };
    }

    public static BootstrapperManifest Sign(BootstrapperManifest boot, string keyId, byte[] privateKey)
    {
        var unsigned = CaligoJson.Serialize(boot with { Signature = null });
        return boot with { Signature = ManifestSigning.Sign(unsigned, keyId, privateKey) };
    }

    public static BootstrapperManifest LoadVerifiedBootstrapper(byte[] json, IReadOnlyDictionary<string, byte[]> keys)
    {
        var text = Encoding.UTF8.GetString(json);
        var boot = CaligoJson.Deserialize<BootstrapperManifest>(text);
        if (!ManifestSigning.Verify(text, boot.Signature, keys))
            throw new SignatureVerificationException("bootstrapper manifest");
        return boot;
    }

    public static ReleaseManifest LoadVerifiedRelease(byte[] json, IReadOnlyDictionary<string, byte[]> keys)
    {
        var text = Encoding.UTF8.GetString(json);
        var release = CaligoJson.Deserialize<ReleaseManifest>(text);
        if (!ManifestSigning.Verify(text, release.Signature, keys))
            throw new SignatureVerificationException("release manifest");
        return release;
    }

    public static ConfigManifest LoadVerifiedConfig(byte[] json, IReadOnlyDictionary<string, byte[]> pinnedKeys)
    {
        var text = Encoding.UTF8.GetString(json);
        var config = CaligoJson.Deserialize<ConfigManifest>(text);
        if (!ManifestSigning.Verify(text, config.Signature, pinnedKeys))
            throw new SignatureVerificationException("config manifest");
        return config;
    }
}
