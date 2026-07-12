namespace CaligoExpress.Test.Support;

using CaligoExpress.Abstractions;
using CaligoExpress.Content;

/// <summary>A scriptable content source for mirror tests: it can be told to fail, and counts hits.</summary>
public sealed class InMemoryContentSource : IContentSource
{
    private readonly Dictionary<string, byte[]> _map;

    /// <summary>The backing key→bytes store, exposed so tests can register artifacts after construction.</summary>
    public Dictionary<string, byte[]> Map => _map;

    public bool Fail { get; set; }
    /// <summary>When set, throws this instead of a 404 — simulates a health failure (timeout/stall/refused).</summary>
    public Exception? FailWith { get; set; }
    public int GetCalls { get; private set; }
    public int DownloadCalls { get; private set; }

    public InMemoryContentSource(Dictionary<string, byte[]>? map = null, bool fail = false)
    {
        _map = map ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Fail = fail;
    }

    public Task<byte[]> GetBytesAsync(string key, CancellationToken ct = default)
    {
        GetCalls++;
        if (FailWith is not null)
            throw FailWith;
        if (Fail)
            throw new ContentNotFoundException(key);
        return _map.TryGetValue(key, out var bytes)
            ? Task.FromResult(bytes)
            : throw new ContentNotFoundException(key);
    }

    public async Task DownloadToFileAsync(
        string key, string destinationPath, string? expectedSha256 = null,
        IProgress<ContentProgress>? progress = null, CancellationToken ct = default)
    {
        DownloadCalls++;
        if (FailWith is not null)
            throw FailWith;
        if (Fail)
            throw new ContentNotFoundException(key);
        if (!_map.TryGetValue(key, out var bytes))
            throw new ContentNotFoundException(key);

        using var src = new MemoryStream(bytes);
        await ContentDownload.CopyVerifiedAsync(src, destinationPath, expectedSha256, bytes.Length, progress, ct);
    }
}
