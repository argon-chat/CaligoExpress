namespace CaligoExpress.Content;

using System.Buffers;
using System.Security.Cryptography;
using CaligoExpress.Abstractions;
using CaligoExpress.Crypto;

/// <summary>Streams a source to a destination file while hashing in one pass, failing closed on mismatch.</summary>
public static class ContentDownload
{
    public static async Task<string> CopyVerifiedAsync(
        Stream source,
        string destinationPath,
        string? expectedSha256,
        long? total,
        IProgress<ContentProgress>? progress,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // 80 KiB, rented from the shared pool (just under the LOH threshold; never allocated per call).
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long received = 0;

        try
        {
            await using var dest = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                hash.AppendData(buffer, 0, n);
                received += n;
                progress?.Report(new ContentProgress(received, total));
            }
            await dest.FlushAsync(ct);
            dest.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (expectedSha256 is not null && !Sha256Hasher.HashesEqual(expectedSha256, actual))
            throw new HashMismatchException(destinationPath, expectedSha256, actual);
        return actual;
    }
}
