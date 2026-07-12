namespace CaligoExpress.Publishing;

using CaligoExpress.Crypto;
using CaligoExpress.Manifests;

/// <summary>
/// Reads back artifacts already laid out in a CDN directory so a release manifest can be assembled from
/// them: it turns a key into a verified <see cref="ArtifactRef"/> (hash+size from the file on disk).
/// </summary>
public sealed class CdnIndex
{
    private readonly string _root;

    public CdnIndex(string cdnRoot) => _root = Path.GetFullPath(cdnRoot);

    public bool Exists(string key) => File.Exists(PathFor(key));

    public ArtifactRef RefFor(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            throw new FileNotFoundException($"artifact not found in CDN: {key}", path);
        var bytes = File.ReadAllBytes(path);
        return new ArtifactRef(key, Sha256Hasher.HashHex(bytes), bytes.Length);
    }

    private string PathFor(string key) => Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
}
