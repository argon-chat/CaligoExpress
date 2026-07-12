namespace CaligoExpress.Engine;

using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using CaligoExpress.Abstractions;
using CaligoExpress.Chunking;
using CaligoExpress.Content;
using CaligoExpress.Crypto;
using CaligoExpress.Diff;
using CaligoExpress.Hooks;
using CaligoExpress.Manifests;
using CaligoExpress.Publishing;
using CaligoExpress.State;
using CaligoExpress.Transaction;
using Serilog;

public enum UpdateOutcome
{
    UpToDate,
    Updated,
}

public sealed record UpdateOptions
{
    public bool AllowRollback { get; init; }

    /// <summary>How many missing chunks to download concurrently during prefetch. Defaults to half the CPU
    /// cores (min 1) — enough to saturate a link without hammering the mirror or the disk.</summary>
    public int DownloadParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
}

public sealed record UpdateResult(UpdateOutcome Outcome, UpdatePlan Plan, int FileOperations);

/// <summary>Coarse progress for UI: a phase plus optional download bytes (summed over chunks).</summary>
public sealed record UpdateProgress(
    string Phase,
    string? Item = null,
    long BytesReceived = 0,
    long? TotalBytes = null,
    double? Fraction = null)
{
    public const string Plan = "plan";
    public const string Download = "download";
    public const string Install = "install";
    public const string Done = "done";
}

/// <summary>
/// Orchestrates a package update with content-defined chunking. After recovering any interrupted
/// transaction and planning against the target release, it materializes every changed file by reusing
/// chunks already on disk (looked up by hash in the installed manifests) and downloading only the chunks
/// it is missing — the "Steam delta." Each file is reassembled and hash-verified into the staging area
/// without touching the live tree, then committed in one journaled, crash-safe transaction. The
/// bootstrapper's own self-update is handled separately, because the running executable is locked.
///
/// The caller must hand in a release manifest whose signature has already been verified against the
/// pinned trust anchors; from there the package-manifest and per-chunk SHA-256 checks chain trust down
/// to every byte (mirrors are untrusted).
/// </summary>
public sealed class UpdateEngine
{
    private readonly InstallPaths _paths;
    private readonly IContentSource _content;
    private readonly StateStore _state;
    private readonly RecoveryService _recovery;
    private readonly ILogger _log;
    private readonly IClock _clock;
    private readonly CdnKeyScheme _keys;

    public UpdateEngine(
        InstallPaths paths,
        IContentSource content,
        StateStore state,
        RecoveryService recovery,
        ILogger? log = null,
        IClock? clock = null,
        CdnKeyScheme? keys = null)
    {
        _paths = paths;
        _content = content;
        _state = state;
        _recovery = recovery;
        _log = log ?? Serilog.Core.Logger.None;
        _clock = clock ?? SystemClock.Instance;
        _keys = keys ?? CdnKeyScheme.Root;
    }

    public async Task<UpdateResult> UpdateAsync(
        ReleaseManifest target,
        UpdateOptions? options = null,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new UpdateOptions();

        _recovery.RecoverPending();
        var current = _state.Read();
        progress?.Report(new UpdateProgress(UpdateProgress.Plan));

        // Release sequences are per-channel, so they only compare within the same channel. Switching
        // channels (e.g. beta → live) is a deliberate move, not a downgrade, even if the destination
        // channel's seq (or version) is lower than what's installed.
        if (string.Equals(target.Channel, current.Channel, StringComparison.OrdinalIgnoreCase)
            && target.ReleaseSeq < current.ReleaseSeq && !options.AllowRollback)
            throw new CaligoException(
                $"downgrade blocked: target release {target.ReleaseSeq} is older than installed {current.ReleaseSeq} on channel {current.Channel}");

        var plan = UpdatePlanner.Plan(target, current, current.BootstrapperVersion);

        if (!plan.Actionable.Any())
        {
            _log.Information("Already up to date at release {Release}", current.ReleaseId);
            return new UpdateResult(UpdateOutcome.UpToDate, plan, 0);
        }

        _paths.EnsureControlDirs();
        var txnId = $"{_clock.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..28];
        var staging = new StagingArea(_paths, txnId);
        var ops = new List<TxnOp>();
        var token = 0;
        string NextToken() => (token++).ToString("D6");

        // Index every chunk already on disk (from the installed package manifests) so unchanged content is
        // copied locally instead of downloaded. This is what makes an update fetch only the difference.
        var localIndex = BuildLocalChunkIndex(current);

        // Fetch + verify the package.json for everything we must sync; it carries the chunk lists.
        var syncing = new List<(PackageRef Pkg, PackageManifest Manifest, string ManifestHash)>();
        foreach (var pp in plan.Packages)
            if (pp.Action == PackageAction.Sync)
            {
                var (mf, hash) = await FetchPackageManifestAsync(pp.Target!, ct);
                syncing.Add((pp.Target!, mf, hash));
            }

        // Pre-scan: the chunks of changed files that are neither already on disk nor cached — i.e. exactly
        // what must come over the wire (deduped by hash). This is the "Steam delta" for this update.
        long totalDownload = 0;
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<(string Hash, int Size)>();
        int changedFiles = 0, unchangedFiles = 0, reusedChunks = 0;
        foreach (var (pkg, mf, _) in syncing)
        {
            var oldHashByPath = OldHashByPath(current, pkg.Name);
            var chunkSize = mf.Chunks.ToDictionary(c => c.Hash, c => c.Size, StringComparer.Ordinal);
            foreach (var file in mf.Files)
            {
                if (IsUnchanged(mf.TargetRoot, file, oldHashByPath))
                {
                    unchangedFiles++;
                    continue;
                }
                changedFiles++;

                // Per-file chunk accounting so the log shows exactly why a file costs what it costs.
                // (Pack chunks are shared by many small files, so the per-file size is the FILE size, not the
                // chunk size — the actual download is deduped in the summary total below.)
                int fReuse = 0, fNew = 0;
                foreach (var r in file.Regions)
                {
                    var local = localIndex.ContainsKey(r.Chunk) || File.Exists(ChunkCachePath(r.Chunk));
                    if (local) { fReuse++; reusedChunks++; }
                    else
                    {
                        fNew++;
                        if (counted.Add(r.Chunk))
                        {
                            missing.Add((r.Chunk, chunkSize[r.Chunk]));
                            totalDownload += chunkSize[r.Chunk];
                        }
                    }
                }
                _log.Debug("  changed {File} ({Size}): {New}/{Total} chunks new, {Reuse} reused",
                    file.Path, Human(file.Size), fNew, file.Regions.Count, fReuse);
            }
        }

        _log.Information(
            "Sync {Release}: {Changed} changed / {Unchanged} unchanged files; chunks {Reused} reused (disk/cache), {Download} to download ({Bytes})",
            target.ReleaseId, changedFiles, unchangedFiles, reusedChunks, missing.Count, Human(totalDownload));

        // Download the missing chunks concurrently into the cache; assembly then reads them back in order.
        if (missing.Count > 0)
        {
            progress?.Report(new UpdateProgress(UpdateProgress.Download, null, 0, totalDownload));
            await PrefetchChunksAsync(missing, totalDownload, options.DownloadParallelism, progress, ct);
        }

        var assembler = new ChunkAssembler(_content, localIndex, ChunkCacheDir, _log, _keys);
        var newPackages = new Dictionary<string, InstalledPackage>(current.Packages, StringComparer.Ordinal);

        progress?.Report(new UpdateProgress(UpdateProgress.Install));
        foreach (var pp in plan.Packages)
        {
            switch (pp.Action)
            {
                case PackageAction.Skip:
                    break;

                case PackageAction.Remove:
                    RemovePackage(current.Packages[pp.Name], staging, ops, NextToken);
                    newPackages.Remove(pp.Name);
                    break;

                case PackageAction.Sync:
                    var entry = syncing.First(s => s.Pkg.Name == pp.Name);
                    newPackages[pp.Name] = await SyncPackageAsync(entry.Pkg, entry.Manifest, entry.ManifestHash, current, assembler, staging, ops, NextToken, ct);
                    break;
            }
        }

        if (ops.Count == 0)
            return new UpdateResult(UpdateOutcome.UpToDate, plan, 0);

        var newState = new LocalInstallState
        {
            SchemaVersion = 1,
            Channel = target.Channel,
            ReleaseSeq = target.ReleaseSeq,
            ReleaseId = target.ReleaseId,
            BootstrapperVersion = current.BootstrapperVersion, // bootstrapper updates via SelfUpdater
            Packages = newPackages,
        };

        var journal = new TransactionJournal
        {
            Id = txnId,
            TargetReleaseSeq = target.ReleaseSeq,
            TargetReleaseId = target.ReleaseId,
            Operations = ops,
            NewState = newState,
        };

        _log.Information("Committing {Ops} file operations for release {Release}", ops.Count, target.ReleaseId);
        progress?.Report(new UpdateProgress(UpdateProgress.Install));
        await new TransactionRunner(_paths, _state).CommitAsync(journal, ct);

        // The update is committed (every chunk now lives in app/), so the transient chunk cache is dead
        // weight — drop it to reclaim disk. It is only kept across runs to resume an interrupted update.
        CleanChunkCache();

        progress?.Report(new UpdateProgress(UpdateProgress.Done));
        return new UpdateResult(UpdateOutcome.Updated, plan, ops.Count);
    }

    private async Task<InstalledPackage> SyncPackageAsync(
        PackageRef pkg, PackageManifest newMf, string manifestHash, LocalInstallState current,
        ChunkAssembler assembler, StagingArea staging, List<TxnOp> ops, Func<string> nextToken, CancellationToken ct)
    {
        var oldHashByPath = OldHashByPath(current, pkg.Name);
        var newPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in newMf.Files)
        {
            newPaths.Add(file.Path);
            if (IsUnchanged(newMf.TargetRoot, file, oldHashByPath))
                continue; // already correct on disk → don't rewrite it

            var tk = nextToken();
            await assembler.ReassembleAsync(file, staging, tk, ct);
            ops.Add(MakeWriteOp(newMf.TargetRoot, file.Path, staging, tk, file.Hash));
        }

        // Files that existed in the old version but not the new one are removed.
        if (current.Packages.TryGetValue(pkg.Name, out var inst) && inst.Manifest is { } oldMf)
            AddDeletions(oldMf, newPaths, staging, ops, nextToken);

        AddHookOps(newMf, staging, ops, nextToken);
        return new InstalledPackage(pkg.Name, pkg.Version, manifestHash, newMf);
    }

    private void RemovePackage(InstalledPackage installed, StagingArea staging, List<TxnOp> ops, Func<string> nextToken)
    {
        if (installed.Manifest is not { } mf)
            return;
        AddDeletions(mf, newPaths: new HashSet<string>(StringComparer.Ordinal), staging, ops, nextToken);
    }

    private void AddDeletions(
        PackageManifest oldMf, HashSet<string> newPaths, StagingArea staging, List<TxnOp> ops, Func<string> nextToken)
    {
        foreach (var file in oldMf.Files)
        {
            if (newPaths.Contains(file.Path))
                continue;
            var target = _paths.ResolveInstalledFile(oldMf.TargetRoot, file.Path);
            if (File.Exists(target))
                ops.Add(new TxnOp
                {
                    Kind = TxnOpKind.Delete,
                    TargetPath = target,
                    BackupPath = staging.BackupPathFor(nextToken()),
                    OldHash = file.Hash,
                });
        }
    }

    private void AddHookOps(PackageManifest mf, StagingArea staging, List<TxnOp> ops, Func<string> nextToken)
    {
        var outputs = HookRunner.Run(mf, rel =>
        {
            var p = _paths.ResolveInstalledFile(mf.TargetRoot, rel);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        });

        foreach (var output in outputs)
        {
            var tk = nextToken();
            staging.WriteStaged(tk, output.Content);
            ops.Add(MakeWriteOp(mf.TargetRoot, output.RelativePath, staging, tk, Sha256Hasher.HashHex(output.Content)));
        }
    }

    private TxnOp MakeWriteOp(string targetRoot, string relPath, StagingArea staging, string token, string newHash)
    {
        var target = _paths.ResolveInstalledFile(targetRoot, relPath);
        return File.Exists(target)
            ? new TxnOp
            {
                Kind = TxnOpKind.Replace,
                TargetPath = target,
                StagedPath = staging.StagePathFor(token),
                BackupPath = staging.BackupPathFor(token),
                NewHash = newHash,
            }
            : new TxnOp
            {
                Kind = TxnOpKind.Add,
                TargetPath = target,
                StagedPath = staging.StagePathFor(token),
                NewHash = newHash,
            };
    }

    private bool IsUnchanged(string targetRoot, FileEntry file, IReadOnlyDictionary<string, string> oldHashByPath)
        => oldHashByPath.TryGetValue(file.Path, out var oldHash)
           && string.Equals(oldHash, file.Hash, StringComparison.OrdinalIgnoreCase)
           && File.Exists(_paths.ResolveInstalledFile(targetRoot, file.Path));

    private static IReadOnlyDictionary<string, string> OldHashByPath(LocalInstallState current, string pkgName)
        => current.Packages.TryGetValue(pkgName, out var inst) && inst.Manifest is { } mf
            ? mf.Files.ToDictionary(f => f.Path, f => f.Hash, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Maps every chunk hash on disk to one place it can be read from (file path + byte range),
    /// derived from the installed package manifests. Any source works; we verify the hash after reading.</summary>
    private Dictionary<string, ChunkSource> BuildLocalChunkIndex(LocalInstallState current)
    {
        var index = new Dictionary<string, ChunkSource>(StringComparer.Ordinal);
        foreach (var inst in current.Packages.Values)
        {
            if (inst.Manifest is not { } mf)
                continue;
            var chunkSize = mf.Chunks.ToDictionary(c => c.Hash, c => c.Size, StringComparer.Ordinal);
            foreach (var file in mf.Files)
            {
                var path = _paths.ResolveInstalledFile(mf.TargetRoot, file.Path);
                long offset = 0;
                foreach (var r in file.Regions)
                {
                    // Only a whole-chunk region is a contiguous slice of this one file, so only those can be
                    // reused off disk by hash. Pack-chunk slices (partial regions) are re-fetched on change.
                    if (r.Offset == 0 && chunkSize.TryGetValue(r.Chunk, out var cs) && r.Length == cs)
                        index.TryAdd(r.Chunk, new ChunkSource(path, offset, r.Length));
                    offset += r.Length;
                }
            }
        }
        return index;
    }

    private string ChunkCacheDir => Path.Combine(_paths.CacheDir, "chunks");
    private string ChunkCachePath(string hash) => Path.Combine(ChunkCacheDir, hash[..2], hash);

    private static string Human(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0.0} KB";

    /// <summary>Deletes the transient chunk cache (best-effort) after a committed update.</summary>
    private void CleanChunkCache()
    {
        try
        {
            if (Directory.Exists(ChunkCacheDir))
                Directory.Delete(ChunkCacheDir, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to clean chunk cache at {Dir}", ChunkCacheDir);
        }
    }

    /// <summary>Downloads the missing chunks into the cache, up to <paramref name="parallelism"/> at a time
    /// (≈ half the cores), reporting cumulative download progress. Each chunk is hash-verified before it is
    /// cached; assembly later reads them back from the cache in file order.</summary>
    private async Task PrefetchChunksAsync(
        IReadOnlyList<(string Hash, int Size)> missing, long total, int parallelism,
        IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        long received = 0;

        var tasks = missing.Select(async chunk =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var cacheFile = ChunkCachePath(chunk.Hash);
                if (File.Exists(cacheFile))
                    return;

                var plain = await DownloadChunkPlaintextAsync(_content, _keys.Chunk(chunk.Hash), chunk.Hash, _log, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllBytesAsync(cacheFile, plain, ct); // cache the decompressed plaintext

                // Count plaintext bytes so the progress fraction matches the manifest's total even though
                // the wire transfer was compressed.
                var r = Interlocked.Add(ref received, chunk.Size);
                progress?.Report(new UpdateProgress(
                    UpdateProgress.Download, null, r, total, total > 0 ? (double)r / total : null));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>Fetches a chunk object, decodes it to plaintext and verifies its hash, retrying transient
    /// failures (DNS/socket/IO blips, or a corrupt body) with exponential backoff. Each attempt goes through
    /// the mirror source, so a retry also re-races/fails-over mirrors. A genuine 404 is not retried.</summary>
    private static async Task<byte[]> DownloadChunkPlaintextAsync(
        IContentSource content, string key, string hash, ILogger log, CancellationToken ct, int maxAttempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var plain = ChunkCodec.Decode(await content.GetBytesAsync(key, ct));
                var actual = Sha256Hasher.HashHex(plain);
                if (!Sha256Hasher.HashesEqual(hash, actual))
                    throw new HashMismatchException(key, hash, actual);
                return plain;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested && IsRetryable(ex))
            {
                var delayMs = 400 * (1 << (attempt - 1)); // 0.4s, 0.8s, 1.6s
                log.Warning("Chunk {Key} fetch failed (attempt {Attempt}/{Max}): {Err}; retrying in {Delay}ms",
                    key, attempt, maxAttempts, ex.Message, delayMs);
                await Task.Delay(delayMs, ct);
            }
        }
    }

    /// <summary>Transient = a network blip or a corrupt fetch (worth another mirror); a real 404 is not.</summary>
    private static bool IsRetryable(Exception ex)
    {
        if (ex is HashMismatchException)
            return true; // poisoned/corrupt object — another mirror may serve a clean one
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException or SocketException or IOException or TimeoutException)
                return true;
        return false;
    }

    private async Task<(PackageManifest Manifest, string Hash)> FetchPackageManifestAsync(PackageRef pkg, CancellationToken ct)
    {
        var bytes = await _content.GetBytesAsync(pkg.Manifest.Key, ct);
        var actual = Sha256Hasher.HashHex(bytes);
        if (!Sha256Hasher.HashesEqual(pkg.Manifest.Hash, actual))
            throw new HashMismatchException(pkg.Manifest.Key, pkg.Manifest.Hash, actual);

        var manifest = CaligoJson.Deserialize<PackageManifest>(System.Text.Encoding.UTF8.GetString(bytes));
        return (manifest, actual);
    }

    private readonly record struct ChunkSource(string Path, long Offset, int Length);

    /// <summary>
    /// Reassembles files from chunks in order: prefer the copy already on disk (verified by hash, so a
    /// drifted file silently falls back), then the prefetched on-disk cache, then a last-resort download
    /// (in case a cache entry went missing). All sources are hash-checked, so mirrors stay untrusted.
    /// The bulk of downloading happens earlier, in the parallel prefetch.
    /// </summary>
    private sealed class ChunkAssembler(
        IContentSource content,
        Dictionary<string, ChunkSource> localIndex,
        string cacheDir,
        ILogger log,
        CdnKeyScheme keys)
    {
        public async Task ReassembleAsync(FileEntry file, StagingArea staging, string token, CancellationToken ct)
        {
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = staging.OpenStagedWrite(token))
            {
                foreach (var region in file.Regions)
                {
                    var chunk = await GetChunkAsync(region.Chunk, ct); // full chunk plaintext
                    await fs.WriteAsync(chunk.AsMemory(region.Offset, region.Length), ct);
                    inc.AppendData(chunk, region.Offset, region.Length);
                }
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }

            var actual = Convert.ToHexStringLower(inc.GetHashAndReset());
            if (!Sha256Hasher.HashesEqual(file.Hash, actual))
                throw new HashMismatchException(file.Path, file.Hash, actual);
        }

        private async Task<byte[]> GetChunkAsync(string hash, CancellationToken ct)
        {
            if (localIndex.TryGetValue(hash, out var src))
            {
                var local = TryReadVerified(src, hash);
                if (local is not null)
                    return local;
            }

            var cacheFile = CachePath(hash);
            if (File.Exists(cacheFile))
            {
                var cached = await File.ReadAllBytesAsync(cacheFile, ct);
                if (Sha256Hasher.HashHex(cached) == hash)
                    return cached;
            }

            // Cache miss (e.g. evicted between prefetch and assembly) — fetch + decode now, with retry.
            var plain = await DownloadChunkPlaintextAsync(content, keys.Chunk(hash), hash, log, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllBytesAsync(cacheFile, plain, ct);
            return plain;
        }

        private static byte[]? TryReadVerified(ChunkSource src, string hash)
        {
            try
            {
                using var fs = new FileStream(src.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Position = src.Offset;
                var buf = new byte[src.Length];
                var read = 0;
                while (read < src.Length)
                {
                    var n = fs.Read(buf, read, src.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return read == src.Length && Sha256Hasher.HashHex(buf) == hash ? buf : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private string CachePath(string hash) => Path.Combine(cacheDir, hash[..2], hash);
    }
}
