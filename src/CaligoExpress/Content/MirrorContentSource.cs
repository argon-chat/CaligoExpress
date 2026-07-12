namespace CaligoExpress.Content;

using CaligoExpress.Abstractions;
using Serilog;

/// <summary>
/// Fans a logical fetch out across an ordered set of mirrors with automatic failover — the fix for the
/// legacy system, which listed many endpoints but only ever used the first. A mirror that fails (refused,
/// times out responding, or streams nothing) is <em>penalized</em>: it is moved to the back of the order
/// for the rest of the session, so a dead/stalled mirror never blocks more than one request instead of
/// stalling every chunk. A 404 is key-specific (the object just isn't on that mirror), so it fails over
/// without penalizing. Because every artifact is content-addressed by hash, mixing mirrors is always safe.
/// </summary>
public sealed class MirrorContentSource : IContentSource
{
    private readonly IReadOnlyList<IContentSource> _mirrors;
    private readonly ILogger _log;
    private readonly HashSet<int> _penalized = [];
    private readonly object _gate = new();

    public MirrorContentSource(IReadOnlyList<IContentSource> mirrors, ILogger? log = null, int raceWidth = 2)
    {
        if (mirrors.Count == 0)
            throw new ArgumentException("at least one mirror is required", nameof(mirrors));
        _mirrors = mirrors;
        _log = log ?? Serilog.Core.Logger.None;
        _ = raceWidth; // kept for source compat; failover is now sequential with health penalties
    }

    public Task<byte[]> GetBytesAsync(string key, CancellationToken ct = default)
        => TryEachAsync(key, (m, c) => m.GetBytesAsync(key, c), ct);

    public Task DownloadToFileAsync(
        string key, string destinationPath, string? expectedSha256 = null,
        IProgress<ContentProgress>? progress = null, CancellationToken ct = default)
        => TryEachAsync<object?>(key, async (m, c) =>
        {
            await m.DownloadToFileAsync(key, destinationPath, expectedSha256, progress, c);
            return null;
        }, ct);

    private async Task<T> TryEachAsync<T>(string key, Func<IContentSource, CancellationToken, Task<T>> fetch, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var i in Order())
        {
            try
            {
                return await fetch(_mirrors[i], ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine caller cancellation, not a mirror timeout
            }
            catch (ContentNotFoundException ex)
            {
                last = ex; // object isn't on this mirror — try the next, but the mirror itself is healthy
            }
            catch (Exception ex)
            {
                last = ex;
                Penalize(i);
                _log.Warning(ex, "Mirror {Index} failed for {Key}; penalizing it and trying the next", i, key);
            }
        }

        throw new ContentNotFoundException(key, last);
    }

    /// <summary>Healthy mirrors first (in priority order), penalized ones last.</summary>
    private IReadOnlyList<int> Order()
    {
        lock (_gate)
        {
            var indices = Enumerable.Range(0, _mirrors.Count);
            return indices.Where(i => !_penalized.Contains(i)).Concat(indices.Where(i => _penalized.Contains(i))).ToList();
        }
    }

    private void Penalize(int i)
    {
        lock (_gate)
            _penalized.Add(i);
    }
}
