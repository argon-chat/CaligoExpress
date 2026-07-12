namespace CaligoExpress.Content;

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using CaligoExpress.Abstractions;
using CaligoExpress.Crypto;
using CaligoExpress.Fs;

/// <summary>
/// A single HTTP mirror. Two timeouts make a dead mirror fail over fast instead of hanging forever:
/// a <em>response</em> timeout (the headers must arrive — catches a TCP connection that opens but never
/// answers) and a <em>stall</em> timeout (the body must keep producing bytes — catches a connection that
/// streams nothing). Both are surfaced as <see cref="TimeoutException"/>/<see cref="IOException"/> (NOT
/// <see cref="OperationCanceledException"/>) so <see cref="MirrorContentSource"/> treats them as failures
/// and moves to the next mirror. Large artifacts download to a <c>.part</c> file with range resume.
/// </summary>
public sealed class HttpContentSource : IContentSource
{
    // 80 KiB — the Stream.CopyTo default, deliberately just under the 85 000-byte Large Object Heap
    // threshold so the (pooled) buffer stays in gen0. Rented from the shared pool, never allocated per call.
    private const int StreamBufferSize = 81920;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _stallTimeout;

    public HttpContentSource(
        string host, HttpClient http, string scheme = "https",
        TimeSpan? responseTimeout = null, TimeSpan? stallTimeout = null)
    {
        _baseUrl = $"{scheme}://{host}".TrimEnd('/');
        _http = http;
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10);
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(12);
    }

    private string Url(string key) => $"{_baseUrl}/{key.TrimStart('/')}";

    public async Task<byte[]> GetBytesAsync(string key, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(key));
        using var resp = await SendWithResponseTimeoutAsync(req, key, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new ContentNotFoundException(key);
        resp.EnsureSuccessStatusCode();

        var len = resp.Content.Headers.ContentLength;
        using var ms = len is > 0 and <= int.MaxValue ? new MemoryStream((int)len.Value) : new MemoryStream();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            while (true)
            {
                var n = await ReadWithStallTimeoutAsync(stream, buffer, stall, key, ct);
                if (n == 0)
                    break;
                ms.Write(buffer, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return ms.ToArray();
    }

    public async Task DownloadToFileAsync(
        string key,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<ContentProgress>? progress = null,
        CancellationToken ct = default)
    {
        var part = destinationPath + ".part";
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, Url(key));
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await SendWithResponseTimeoutAsync(req, key, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new ContentNotFoundException(key);
        resp.EnsureSuccessStatusCode();

        // If the server ignored our Range and sent the whole file, start the part over.
        var append = resp.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        if (!append && File.Exists(part))
            File.Delete(part);

        var total = (append ? existing : 0) + (resp.Content.Headers.ContentLength ?? -1);
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
            try
            {
                var received = append ? existing : 0;
                while (true)
                {
                    var n = await ReadWithStallTimeoutAsync(stream, buffer, stall, key, ct);
                    if (n == 0)
                        break;
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    received += n;
                    progress?.Report(new ContentProgress(received, total > 0 ? total : null));
                }
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (expectedSha256 is not null)
        {
            var actual = await Sha256Hasher.HashFileAsync(part, ct);
            if (!Sha256Hasher.HashesEqual(expectedSha256, actual))
            {
                File.Delete(part); // poisoned part: force a clean retry on the next mirror
                throw new HashMismatchException(key, expectedSha256, actual);
            }
        }

        AtomicFile.Move(part, destinationPath, overwrite: true);
    }

    /// <summary>GET with a response-headers timeout. A dead mirror (TCP up, no HTTP answer) trips this and
    /// surfaces a <see cref="TimeoutException"/> so the caller fails over instead of hanging.</summary>
    private async Task<HttpResponseMessage> SendWithResponseTimeoutAsync(HttpRequestMessage req, string key, CancellationToken ct)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(_responseTimeout);
        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no response from {_baseUrl} within {_responseTimeout.TotalSeconds:0}s for '{key}'");
        }
    }

    /// <summary>One stream read with a stall timeout (re-armed per read). A connection that streams nothing
    /// trips this and surfaces an <see cref="IOException"/> so the caller fails over.</summary>
    private async Task<int> ReadWithStallTimeoutAsync(Stream stream, byte[] buffer, CancellationTokenSource stall, string key, CancellationToken ct)
    {
        stall.CancelAfter(_stallTimeout);
        try
        {
            return await stream.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"download stalled (no data for {_stallTimeout.TotalSeconds:0}s) from {_baseUrl} for '{key}'");
        }
    }
}
