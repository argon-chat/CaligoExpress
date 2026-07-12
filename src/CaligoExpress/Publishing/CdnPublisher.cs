namespace CaligoExpress.Publishing;

using System.Text;
using CaligoExpress.Chunking;
using CaligoExpress.Crypto;
using CaligoExpress.Manifests;

/// <summary>
/// Writes artifacts into a CDN/S3-style directory tree under the canonical <see cref="CdnKeys"/>
/// scheme and returns the <see cref="ArtifactRef"/> for the package manifest, ready to wire into a
/// release. Chunk objects are content-addressed, so writes are idempotent and shared chunks are written
/// once. Releases and configs are written already-signed.
/// </summary>
public sealed class CdnPublisher
{
    private readonly string _root;

    public CdnPublisher(string cdnRoot)
    {
        _root = Path.GetFullPath(cdnRoot);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Writes the package's missing chunk objects and its package.json; returns the manifest ref.</summary>
    public ArtifactRef PublishPackage(PackedPackage package)
    {
        foreach (var (hash, bytes) in package.Chunks)
        {
            var path = PathFor(CdnKeys.Chunk(hash));
            if (!File.Exists(path)) // content-addressed → already-present chunks need no rewrite (dedup)
                File.WriteAllBytes(path, ChunkCodec.Encode(bytes));
        }

        return WriteArtifact(
            CdnKeys.PackageManifest(package.Manifest.Name, package.Manifest.Version),
            Encoding.UTF8.GetBytes(CaligoJson.Serialize(package.Manifest)));
    }

    public ArtifactRef PublishBootstrapper(string version, byte[] bytes)
        => WriteArtifact(CdnKeys.Bootstrapper(version), bytes);

    /// <summary>Uploads the macOS whole-app bundle archive (a <c>ditto -c -k --keepParent</c> zip) and
    /// returns its content-addressed ref, ready to wrap in a <see cref="MonolithRef"/> on a release.</summary>
    public ArtifactRef PublishMacApp(string version, byte[] zipBytes)
        => WriteArtifact(CdnKeys.MacApp(version), zipBytes);

    public void PublishRelease(ReleaseManifest signedRelease)
        => WriteText(CdnKeys.Release(signedRelease.Channel), CaligoJson.Serialize(signedRelease));

    public void PublishBootstrapperManifest(BootstrapperManifest signedBoot)
        => WriteText(CdnKeys.BootstrapperManifest(signedBoot.Channel), CaligoJson.Serialize(signedBoot));

    public void PublishConfig(ConfigManifest signedConfig)
        => WriteText(CdnKeys.Config, CaligoJson.Serialize(signedConfig));

    private ArtifactRef WriteArtifact(string key, byte[] bytes)
    {
        File.WriteAllBytes(PathFor(key), bytes);
        return new ArtifactRef(key, Sha256Hasher.HashHex(bytes), bytes.Length);
    }

    private void WriteText(string key, string text) => File.WriteAllText(PathFor(key), text);

    private string PathFor(string key)
    {
        var full = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }
}
