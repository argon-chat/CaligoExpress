namespace CaligoExpress.Content;

using CaligoExpress.Abstractions;
using CaligoExpress.Fs;

/// <summary>
/// Serves content from a local directory laid out like the CDN (key == relative path). This is what
/// makes the whole engine testable offline: the CLI can build a fake CDN tree and the engine runs the
/// real update flow against it with zero network.
/// </summary>
public sealed class LocalContentSource : IContentSource
{
    private readonly string _root;

    public LocalContentSource(string rootDir) => _root = Path.GetFullPath(rootDir);

    public async Task<byte[]> GetBytesAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
            throw new ContentNotFoundException(key);
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task DownloadToFileAsync(
        string key,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<ContentProgress>? progress = null,
        CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
            throw new ContentNotFoundException(key);

        await using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await ContentDownload.CopyVerifiedAsync(src, destinationPath, expectedSha256, src.Length, progress, ct);
    }

    private string Resolve(string key) => SafePath.ResolveUnder(_root, key);
}
