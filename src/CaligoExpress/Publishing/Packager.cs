namespace CaligoExpress.Publishing;

using CaligoExpress.Chunking;
using CaligoExpress.Crypto;
using CaligoExpress.Manifests;

/// <summary>A packed package: the file-listing manifest (each file expressed as chunk-slice regions) plus
/// the unique chunk objects (hash → plaintext bytes) to upload. Identical chunks within the build collapse
/// to a single entry; chunks already on the CDN from earlier versions are skipped at upload.</summary>
public sealed record PackedPackage(PackageManifest Manifest, IReadOnlyDictionary<string, byte[]> Chunks);

/// <summary>
/// Publisher-side: turns a build directory into a package manifest + its content-defined chunks. Big files
/// are CDC-chunked on their own (every region is a whole chunk, so the client can reuse unchanged ones
/// straight off disk). Small files (under <c>packThreshold</c>) are concatenated into one stream and
/// CDC-chunked together, so hundreds of tiny files collapse into a handful of shared "pack" chunks — far
/// fewer billed S3 objects. Either way there is no previous build needed at publish time.
/// </summary>
public static class Packager
{
    /// <summary>Files smaller than this are glued into shared pack chunks instead of getting their own.</summary>
    public const int DefaultPackThreshold = 1024 * 1024;

    public static PackedPackage Pack(
        string sourceDir, string name, string version, string targetRoot = "app",
        IEnumerable<HookDef>? hooks = null, ChunkerOptions? chunker = null, int packThreshold = DefaultPackThreshold)
    {
        var root = Path.GetFullPath(sourceDir);
        var all = EnumerateFilesStable(root)
            .Select(f => (Rel: Path.GetRelativePath(root, f).Replace('\\', '/'), Bytes: File.ReadAllBytes(f)))
            .ToList();

        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var chunkList = new List<ChunkEntry>();
        var files = new List<FileEntry>();

        void AddChunk(string hash, ReadOnlySpan<byte> bytes)
        {
            if (chunks.ContainsKey(hash))
                return;
            chunks[hash] = bytes.ToArray();
            chunkList.Add(new ChunkEntry(hash, bytes.Length));
        }

        // ── big files: solo CDC chunks (every region is a whole chunk) ──
        foreach (var (rel, bytes) in all.Where(x => x.Bytes.Length >= packThreshold))
        {
            var regions = new List<RegionEntry>();
            foreach (var span in Chunker.Split(bytes, chunker))
            {
                AddChunk(span.Hash, bytes.AsSpan((int)span.Offset, span.Length));
                regions.Add(new RegionEntry(span.Hash, 0, span.Length));
            }
            files.Add(new FileEntry(rel, Sha256Hasher.HashHex(bytes), bytes.Length, regions));
        }

        // ── small files: concatenated into one stream, CDC-chunked into shared pack chunks ──
        var smalls = all.Where(x => x.Bytes.Length < packThreshold).ToList(); // already path-sorted
        if (smalls.Count > 0)
        {
            var pack = new byte[smalls.Sum(x => (long)x.Bytes.Length)];
            var ranges = new List<(string Rel, string Hash, int Start, int Len)>(smalls.Count);
            var pos = 0;
            foreach (var (rel, bytes) in smalls)
            {
                bytes.CopyTo(pack, pos);
                ranges.Add((rel, Sha256Hasher.HashHex(bytes), pos, bytes.Length));
                pos += bytes.Length;
            }

            var spans = Chunker.Split(pack, chunker);
            foreach (var span in spans)
                AddChunk(span.Hash, pack.AsSpan((int)span.Offset, span.Length));

            // Each small file's regions are the pack-chunk slices overlapping its [Start, Start+Len) range.
            // Both files and spans are in increasing stream order, so a single monotonic cursor suffices.
            var si = 0;
            foreach (var (rel, hash, start, len) in ranges)
            {
                var end = start + len;
                while (si < spans.Count && spans[si].Offset + spans[si].Length <= start)
                    si++;

                var regions = new List<RegionEntry>();
                for (var j = si; j < spans.Count && spans[j].Offset < end; j++)
                {
                    var s = spans[j];
                    var ovStart = Math.Max(start, (int)s.Offset);
                    var ovEnd = Math.Min(end, (int)s.Offset + s.Length);
                    regions.Add(new RegionEntry(s.Hash, ovStart - (int)s.Offset, ovEnd - ovStart));
                }
                files.Add(new FileEntry(rel, hash, len, regions));
            }
        }

        files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var manifest = new PackageManifest(1, name, version, targetRoot, chunkList, files, (hooks ?? []).ToList());
        return new PackedPackage(manifest, chunks);
    }

    private static IEnumerable<string> EnumerateFilesStable(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);
}
