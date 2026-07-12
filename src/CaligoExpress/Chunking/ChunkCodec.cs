namespace CaligoExpress.Chunking;

using System.IO.Compression;
using CaligoExpress.Abstractions;

/// <summary>
/// On-the-wire/at-rest encoding of a chunk object: a single codec-tag byte followed by the payload.
/// Chunks are content-addressed by the SHA-256 of their <em>plaintext</em> (so reassembly and dedup are
/// unaffected by how they're stored); the tag tells the client how to recover that plaintext. A chunk
/// that doesn't compress (already-packed assets, encrypted data) is stored raw, so compression never
/// makes an object bigger than the original + 1 byte.
/// </summary>
public static class ChunkCodec
{
    private const byte Raw = 0;
    private const byte Gzip = 1;

    /// <summary>Encodes plaintext chunk bytes into a stored object (compressed if that's actually smaller).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> plain)
    {
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(plain);
            compressed = ms.ToArray();
        }

        if (compressed.Length + 1 < plain.Length)
        {
            var outp = new byte[compressed.Length + 1];
            outp[0] = Gzip;
            compressed.CopyTo(outp.AsSpan(1));
            return outp;
        }

        var raw = new byte[plain.Length + 1];
        raw[0] = Raw;
        plain.CopyTo(raw.AsSpan(1));
        return raw;
    }

    /// <summary>Decodes a stored chunk object back to its plaintext bytes.</summary>
    public static byte[] Decode(byte[] stored)
    {
        if (stored.Length == 0)
            return [];

        switch (stored[0])
        {
            case Raw:
                return stored.AsSpan(1).ToArray();
            case Gzip:
                using (var input = new MemoryStream(stored, 1, stored.Length - 1))
                using (var gz = new GZipStream(input, CompressionMode.Decompress))
                using (var outp = new MemoryStream())
                {
                    gz.CopyTo(outp);
                    return outp.ToArray();
                }
            default:
                throw new CaligoException($"unknown chunk codec tag {stored[0]}");
        }
    }
}
