namespace CaligoExpress.Chunking;

using CaligoExpress.Crypto;

/// <summary>Content-defined chunking parameters. Must stay stable across versions for cross-version
/// dedup to work (identical boundaries → identical chunk hashes). The client never runs the chunker,
/// so it never needs these; only the publisher chunks files.</summary>
public sealed record ChunkerOptions(int Min, int Avg, int Max)
{
    /// <summary>Production default: ~10 MiB average (5–20 MiB). Smaller chunks localize partial changes
    /// (e.g. an Authenticode re-sign only dirties the first+last chunk of a multi-chunk binary instead of
    /// re-downloading the whole file), at the cost of more chunk objects. A file smaller than Min is still
    /// a single chunk, so files under ~10 MiB don't benefit from this — those need the change to be smaller
    /// (don't re-sign unchanged vendor DLLs; reproducible bundles), not a smaller chunk size.</summary>
    public static readonly ChunkerOptions Default = new(5 * 1024 * 1024, 10 * 1024 * 1024, 20 * 1024 * 1024);

    internal int Bits
    {
        get
        {
            var bits = 0;
            for (var v = Avg; v > 1; v >>= 1) bits++;
            return bits;
        }
    }
}

/// <summary>One content-defined chunk of a file: its byte range and the SHA-256 of its bytes.</summary>
public readonly record struct ChunkSpan(long Offset, int Length, string Hash);

/// <summary>
/// FastCDC-style content-defined chunking with a rolling Gear hash. Boundaries depend only on a short
/// window of preceding bytes, so an edit shifts at most the chunks around it — everything else keeps
/// the same hashes and is reused (the "Steam delta" emerges as the set difference of chunk hashes).
/// The gear table is generated from a fixed seed, so chunking is fully deterministic across machines.
/// </summary>
public static class Chunker
{
    private static readonly ulong[] Gear = BuildGearTable();

    public static List<ChunkSpan> Split(byte[] data, ChunkerOptions? options = null)
    {
        var opts = options ?? ChunkerOptions.Default;
        var spans = new List<ChunkSpan>();
        var start = 0;
        while (start < data.Length)
        {
            var len = CutPoint(data, start, opts);
            var hash = Sha256Hasher.HashHex(data.AsSpan(start, len));
            spans.Add(new ChunkSpan(start, len, hash));
            start += len;
        }

        // An empty file has no chunks; callers treat that as a zero-byte file (no downloads needed).
        return spans;
    }

    /// <summary>Length of the next chunk starting at <paramref name="start"/>, in [Min, Max] (except a
    /// trailing short remainder).</summary>
    private static int CutPoint(byte[] data, int start, ChunkerOptions opts)
    {
        var remaining = data.Length - start;
        if (remaining <= opts.Min)
            return remaining;

        var bits = opts.Bits;
        // Two masks: a stricter one below the average size (rarer cuts → grow toward the average) and a
        // looser one above it (more frequent cuts → cap the size). This concentrates sizes near Avg.
        var maskS = (1UL << (bits + 1)) - 1;
        var maskL = (1UL << (bits - 1)) - 1;

        var max = Math.Min(remaining, opts.Max);
        var normal = Math.Min(opts.Avg, max);

        ulong hash = 0;
        for (var i = 0; i < max; i++)
        {
            hash = (hash << 1) + Gear[data[start + i]];
            if (i < opts.Min)
                continue; // never cut before the minimum size
            var mask = i < normal ? maskS : maskL;
            if ((hash & mask) == 0)
                return i + 1;
        }

        return max;
    }

    private static ulong[] BuildGearTable()
    {
        // SplitMix64 from a fixed seed — deterministic, well-distributed, identical everywhere.
        var table = new ulong[256];
        var state = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < 256; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }
        return table;
    }
}
