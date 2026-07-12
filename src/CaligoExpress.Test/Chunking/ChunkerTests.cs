namespace CaligoExpress.Test.Chunking;

using CaligoExpress.Chunking;
using CaligoExpress.Crypto;
using CaligoExpress.Test.Support;

[TestFixture]
public class ChunkerTests
{
    private static readonly ChunkerOptions Opts = new(512, 2048, 8192);

    [Test]
    public void Split_IsDeterministic_AndReassembles()
    {
        var data = TreeFixture.Buffer(200_000, seed: 7);

        var a = Chunker.Split(data, Opts);
        var b = Chunker.Split(data, Opts);

        Assert.That(a.Select(c => c.Hash), Is.EqualTo(b.Select(c => c.Hash)), "chunking must be deterministic");
        Assert.That(a.Sum(c => (long)c.Length), Is.EqualTo(data.Length), "chunks must cover the whole input");

        // Concatenating the chunk byte-ranges reproduces the input exactly.
        var rebuilt = new byte[data.Length];
        var pos = 0;
        foreach (var c in a)
        {
            data.AsSpan((int)c.Offset, c.Length).CopyTo(rebuilt.AsSpan(pos));
            pos += c.Length;
            Assert.That(Sha256Hasher.HashHex(data.AsSpan((int)c.Offset, c.Length)), Is.EqualTo(c.Hash));
        }
        Assert.That(rebuilt, Is.EqualTo(data));
    }

    [Test]
    public void Split_RespectsSizeBounds_ExceptTrailingChunk()
    {
        var data = TreeFixture.Buffer(200_000, seed: 9);
        var chunks = Chunker.Split(data, Opts);

        Assert.That(chunks.Count, Is.GreaterThan(10), "a 200 KB input with ~2 KB average should make many chunks");
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.That(chunks[i].Length, Is.LessThanOrEqualTo(Opts.Max));
            if (i < chunks.Count - 1) // the last chunk may be a short remainder
                Assert.That(chunks[i].Length, Is.GreaterThanOrEqualTo(Opts.Min));
        }
    }

    [Test]
    public void Split_LocalizesAnEdit_MostChunksUnchanged()
    {
        var v1 = TreeFixture.Buffer(200_000, seed: 11);
        var v2 = TreeFixture.Buffer(200_000, seed: 11, flipFrom: 90_000, flipLen: 64); // tiny edit in the middle

        var h1 = Chunker.Split(v1, Opts).Select(c => c.Hash).ToHashSet();
        var c2 = Chunker.Split(v2, Opts);

        var reused = c2.Count(c => h1.Contains(c.Hash));
        // Content-defined boundaries resync after the edit, so the overwhelming majority of chunks are shared.
        Assert.That(reused, Is.GreaterThan((int)(c2.Count * 0.8)),
            $"only {c2.Count - reused}/{c2.Count} chunks should differ after a 64-byte edit");
    }

    [Test]
    public void Split_SmallInput_IsOneChunk()
    {
        var small = TreeFixture.Bytes("hello world");
        var chunks = Chunker.Split(small, Opts);
        Assert.That(chunks, Has.Count.EqualTo(1));
        Assert.That(chunks[0].Length, Is.EqualTo(small.Length));
        Assert.That(chunks[0].Hash, Is.EqualTo(Sha256Hasher.HashHex(small)));
    }

    [Test]
    public void Split_Empty_IsNoChunks()
    {
        Assert.That(Chunker.Split([], Opts), Is.Empty);
    }

    [Test]
    public void ChunkCodec_RoundTrips_AndShrinksCompressibleData()
    {
        var compressible = System.Text.Encoding.UTF8.GetBytes(new string('a', 50_000) + "the quick brown fox");
        var stored = ChunkCodec.Encode(compressible);

        Assert.That(stored.Length, Is.LessThan(compressible.Length), "repetitive data should compress");
        Assert.That(ChunkCodec.Decode(stored), Is.EqualTo(compressible));
    }

    [Test]
    public void ChunkCodec_StoresIncompressibleDataRaw_WithoutGrowing()
    {
        var random = TreeFixture.Buffer(40_000, seed: 3);
        var stored = ChunkCodec.Encode(random);

        // Random data can't compress, so it's stored raw: exactly the payload plus the 1-byte codec tag.
        Assert.That(stored.Length, Is.EqualTo(random.Length + 1));
        Assert.That(ChunkCodec.Decode(stored), Is.EqualTo(random));
    }
}
