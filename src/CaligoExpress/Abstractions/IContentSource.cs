namespace CaligoExpress.Abstractions;

/// <summary>Progress for a single artifact download.</summary>
public sealed record ContentProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}

/// <summary>
/// Abstracts "fetch content by key" so the engine is identical over HTTP mirrors and a local
/// directory. Keys are mirror-relative paths from the manifests (e.g. "core/3.4.1/core.zip").
/// Implementations own transport concerns (mirror racing, failover, resume); callers own trust
/// (verifying against manifest hashes/signatures), though <see cref="DownloadToFileAsync"/> may be
/// given an expected hash to verify-and-retry close to the wire.
/// </summary>
public interface IContentSource
{
    /// <summary>Fetches a small document (a manifest) fully into memory.</summary>
    Task<byte[]> GetBytesAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Downloads a (possibly large) artifact to <paramref name="destinationPath"/>. When
    /// <paramref name="expectedSha256"/> is supplied the result is verified and a mismatch throws
    /// <see cref="HashMismatchException"/>.
    /// </summary>
    Task DownloadToFileAsync(
        string key,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<ContentProgress>? progress = null,
        CancellationToken ct = default);
}
