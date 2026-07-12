namespace CaligoExpress.Test.Support;

using CaligoExpress.Crypto;
using CaligoExpress.Manifests;

/// <summary>Helpers for building in-memory file trees and their package manifests in tests.</summary>
public static class TreeFixture
{
    public static Dictionary<string, byte[]> Tree() => new(StringComparer.Ordinal);

    public static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    /// <summary>Deterministic pseudo-random buffer, optionally with a run of bytes flipped.</summary>
    public static byte[] Buffer(int size, int seed, int flipFrom = -1, int flipLen = 0)
    {
        var rand = new Random(seed);
        var buf = new byte[size];
        rand.NextBytes(buf);
        for (var i = flipFrom; flipFrom >= 0 && i < flipFrom + flipLen && i < size; i++)
            buf[i] ^= 0xFF;
        return buf;
    }

    public static PackageManifest ManifestFor(
        string name, string version, IReadOnlyDictionary<string, byte[]> tree, string targetRoot = "app")
    {
        var files = new List<FileEntry>();
        var chunks = new List<ChunkEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in tree.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var hash = Sha256Hasher.HashHex(kv.Value);
            if (seen.Add(hash))
                chunks.Add(new ChunkEntry(hash, kv.Value.Length));
            files.Add(new FileEntry(kv.Key, hash, kv.Value.Length, [new RegionEntry(hash, 0, kv.Value.Length)]));
        }
        return new PackageManifest(1, name, version, targetRoot, chunks, files, []);
    }

    public static bool TreesEqual(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var other) || !v.AsSpan().SequenceEqual(other))
                return false;
        return true;
    }
}
