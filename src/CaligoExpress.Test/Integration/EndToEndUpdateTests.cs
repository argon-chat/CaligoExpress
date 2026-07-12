namespace CaligoExpress.Test.Integration;

using CaligoExpress.Abstractions;
using CaligoExpress.Chunking;
using CaligoExpress.Content;
using CaligoExpress.Crypto;
using CaligoExpress.Diff;
using CaligoExpress.Engine;
using CaligoExpress.Manifests;
using CaligoExpress.Publishing;
using CaligoExpress.State;
using CaligoExpress.Test.Support;
using CaligoExpress.Transaction;

/// <summary>
/// The crown-jewel dogfood: publish two versions to a local "CDN" directory (content-defined chunks +
/// signed manifests, no network), install the first, then update to the second and assert (a) the
/// installed tree is byte-identical to the v2 build and (b) the update downloaded only the chunks that
/// actually changed — every unchanged chunk was reused straight from the v1 files on disk. That second
/// assertion is the whole point of the Steam-style model: no previous archive at publish time, and the
/// client fetches just the difference.
/// </summary>
[TestFixture]
public class EndToEndUpdateTests
{
    private const string KeyId = "k1";

    // Deliberately tiny chunks so a 64 KB file splits into ~30 chunks and a small edit only invalidates a
    // couple of them — making the reuse visible. Production uses ChunkerOptions.Default (~1 MiB).
    private static readonly ChunkerOptions Tiny = new(512, 2048, 8192);

    private string _work = "";
    private string _cdn = "";
    private string _install = "";
    private byte[] _priv = [];
    private Dictionary<string, byte[]> _keys = new();

    [SetUp]
    public void SetUp()
    {
        _work = Path.Combine(Path.GetTempPath(), "caligo-e2e-" + Guid.NewGuid().ToString("N"));
        _cdn = Path.Combine(_work, "cdn");
        _install = Path.Combine(_work, "install");
        Directory.CreateDirectory(_cdn);
        Directory.CreateDirectory(_install);

        var (priv, pub) = Ed25519.GenerateKeyPair();
        _priv = priv;
        _keys = new Dictionary<string, byte[]> { [KeyId] = pub };
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task FreshInstall_ThenChunkUpdate_ReusesUnchangedChunks()
    {
        // ── Build two versions of the app on disk ──────────────────────────────
        var v1 = TreeFixture.Tree();
        v1["big.bin"] = TreeFixture.Buffer(65536, seed: 1);   // dominates size; mostly stable across versions
        v1["keep.txt"] = TreeFixture.Bytes("stable");
        v1["remove.txt"] = TreeFixture.Bytes("delete me in v2");

        var v2 = TreeFixture.Tree();
        v2["big.bin"] = TreeFixture.Buffer(65536, seed: 1, flipFrom: 30000, flipLen: 200); // ~200 bytes changed
        v2["keep.txt"] = TreeFixture.Bytes("stable");
        v2["added.txt"] = TreeFixture.Bytes("brand new in v2");

        var v1Dir = WriteTree("v1", v1);
        var v2Dir = WriteTree("v2", v2);

        var publisher = new CdnPublisher(_cdn);
        var counting = new CountingContentSource(new LocalContentSource(_cdn));
        var paths = new InstallPaths(_install);
        var state = new StateStore(paths);
        var engine = new UpdateEngine(paths, counting, state, new RecoveryService(paths, state));

        // ── Publish v1 (release seq 1) and install it (fresh → fetch all chunks) ──
        // packThreshold 256 keeps big.bin solo-chunked (so we exercise whole-file chunk reuse) while the
        // tiny text files get packed.
        var mf1 = publisher.PublishPackage(Packager.Pack(v1Dir, "core", "1.0.0", chunker: Tiny, packThreshold: 256));
        publisher.PublishRelease(Release(1, "rel-1", new PackageRef("core", "1.0.0", true, [], mf1)));

        var r1 = await LoadAsync(counting);
        var install = await engine.UpdateAsync(r1);
        Assert.That(install.Outcome, Is.EqualTo(UpdateOutcome.Updated));
        Assert.That(install.Plan.Packages.Single(p => p.Name == "core").Action, Is.EqualTo(PackageAction.Sync));
        Assert.That(TreeFixture.TreesEqual(ReadInstalledTree(paths), v1), Is.True, "v1 install must match v1 build");

        // ── Publish v2 (release seq 2) — no delta artifact, no previous archive needed ──
        var mf2 = publisher.PublishPackage(Packager.Pack(v2Dir, "core", "2.0.0", chunker: Tiny, packThreshold: 256));
        publisher.PublishRelease(Release(2, "rel-2", new PackageRef("core", "2.0.0", true, [], mf2)));

        // Update to v2: count only the chunk bytes pulled from the "CDN".
        counting.Reset();
        var r2 = await LoadAsync(counting);
        var update = await engine.UpdateAsync(r2);

        Assert.That(update.Outcome, Is.EqualTo(UpdateOutcome.Updated));
        Assert.That(update.Plan.Packages.Single(p => p.Name == "core").Action, Is.EqualTo(PackageAction.Sync));
        Assert.That(TreeFixture.TreesEqual(ReadInstalledTree(paths), v2), Is.True, "update must reproduce v2 exactly");

        // Only changed files are written: big.bin (replace), added.txt (add), remove.txt (delete);
        // keep.txt is untouched.
        Assert.That(update.FileOperations, Is.EqualTo(3));

        // The dedup payoff: the update downloaded far less than the whole package, because the unchanged
        // chunks of big.bin were copied from the v1 file on disk instead of fetched.
        var v2Bytes = v2.Values.Sum(b => (long)b.Length);
        Assert.That(counting.ChunkBytes, Is.GreaterThan(0), "the changed chunk(s) must be downloaded");
        Assert.That(counting.ChunkBytes, Is.LessThan(v2Bytes / 2),
            $"update pulled {counting.ChunkBytes} of {v2Bytes} bytes — unchanged chunks should be reused from disk");

        // State reflects the new release, and re-running is a no-op.
        Assert.That(state.Read().ReleaseId, Is.EqualTo("rel-2"));
        var again = await engine.UpdateAsync(r2);
        Assert.That(again.Outcome, Is.EqualTo(UpdateOutcome.UpToDate));
        Assert.That(again.FileOperations, Is.EqualTo(0));
    }

    [Test]
    public async Task DowngradeIsBlockedUnlessAllowed()
    {
        var v1 = TreeFixture.Tree();
        v1["f.txt"] = TreeFixture.Bytes("one");
        var v2 = TreeFixture.Tree();
        v2["f.txt"] = TreeFixture.Bytes("two");
        var v1Dir = WriteTree("v1", v1);
        var v2Dir = WriteTree("v2", v2);

        var publisher = new CdnPublisher(_cdn);

        var p1 = publisher.PublishPackage(Packager.Pack(v1Dir, "core", "1.0.0", chunker: Tiny));
        var rel1 = Release(1, "rel-1", new PackageRef("core", "1.0.0", true, [], p1));
        var p2 = publisher.PublishPackage(Packager.Pack(v2Dir, "core", "2.0.0", chunker: Tiny));
        var rel2 = Release(2, "rel-2", new PackageRef("core", "2.0.0", true, [], p2));

        var content = new LocalContentSource(_cdn);
        var paths = new InstallPaths(_install);
        var state = new StateStore(paths);
        var engine = new UpdateEngine(paths, content, state, new RecoveryService(paths, state));

        // Install seq 2 first.
        publisher.PublishRelease(rel2);
        await engine.UpdateAsync(await LoadAsync(content));

        // Point the channel back at seq 1 and try to "update": must be refused.
        publisher.PublishRelease(rel1);
        var r1 = await LoadAsync(content);
        Assert.ThrowsAsync<CaligoException>(async () => await engine.UpdateAsync(r1));

        // ...unless rollback is explicitly allowed.
        var rolled = await engine.UpdateAsync(r1, new UpdateOptions { AllowRollback = true });
        Assert.That(rolled.Outcome, Is.EqualTo(UpdateOutcome.Updated));
        Assert.That(TreeFixture.TreesEqual(ReadInstalledTree(paths), v1), Is.True);
    }

    [Test]
    public async Task SmallFiles_ArePackedIntoSharedChunks()
    {
        var tree = TreeFixture.Tree();
        for (var i = 0; i < 20; i++)
            tree[$"f{i:00}.txt"] = TreeFixture.Bytes($"small file number {i}");
        var dir = WriteTree("v1", tree);

        // 20 tiny files glue into a handful of shared chunks, not 20 separate objects.
        var packed = Packager.Pack(dir, "core", "1.0.0", chunker: Tiny);
        Assert.That(packed.Manifest.Files, Has.Count.EqualTo(20));
        Assert.That(packed.Manifest.Chunks.Count, Is.LessThan(20), "small files must share pack chunks");

        // ...and the package still installs byte-for-byte.
        var publisher = new CdnPublisher(_cdn);
        var mf = publisher.PublishPackage(packed);
        publisher.PublishRelease(Release(1, "rel-1", new PackageRef("core", "1.0.0", true, [], mf)));

        var content = new LocalContentSource(_cdn);
        var paths = new InstallPaths(_install);
        var state = new StateStore(paths);
        var engine = new UpdateEngine(paths, content, state, new RecoveryService(paths, state));
        await engine.UpdateAsync(await LoadAsync(content));

        Assert.That(TreeFixture.TreesEqual(ReadInstalledTree(paths), tree), Is.True);
    }

    [Test]
    public async Task Update_RetriesTransientChunkFailures()
    {
        var v1 = TreeFixture.Tree();
        v1["a.txt"] = TreeFixture.Bytes("alpha");
        v1["b.txt"] = TreeFixture.Bytes("bravo");
        var v1Dir = WriteTree("v1", v1);

        var publisher = new CdnPublisher(_cdn);
        var flaky = new FlakyContentSource(new LocalContentSource(_cdn));
        var paths = new InstallPaths(_install);
        var state = new StateStore(paths);
        var engine = new UpdateEngine(paths, flaky, state, new RecoveryService(paths, state));

        var mf = publisher.PublishPackage(Packager.Pack(v1Dir, "core", "1.0.0", chunker: Tiny));
        publisher.PublishRelease(Release(1, "rel-1", new PackageRef("core", "1.0.0", true, [], mf)));

        // Every chunk's first fetch fails with a transient network error; the engine must retry and succeed.
        var result = await engine.UpdateAsync(await LoadAsync(flaky));

        Assert.That(result.Outcome, Is.EqualTo(UpdateOutcome.Updated));
        Assert.That(flaky.Injected, Is.GreaterThan(0), "the test must actually inject failures");
        Assert.That(TreeFixture.TreesEqual(ReadInstalledTree(paths), v1), Is.True, "retries must produce a correct install");
    }

    [Test]
    public async Task ChannelSwitch_IsNotBlockedAsDowngrade()
    {
        var beta = TreeFixture.Tree();
        beta["f.txt"] = TreeFixture.Bytes("beta build");
        var live = TreeFixture.Tree();
        live["f.txt"] = TreeFixture.Bytes("live build");
        var betaDir = WriteTree("beta", beta);
        var liveDir = WriteTree("live", live);

        var publisher = new CdnPublisher(_cdn);
        var content = new LocalContentSource(_cdn);
        var paths = new InstallPaths(_install);
        var state = new StateStore(paths);
        var engine = new UpdateEngine(paths, content, state, new RecoveryService(paths, state));

        // Install beta at a high seq.
        var betaMf = publisher.PublishPackage(Packager.Pack(betaDir, "core", "9.0.0", chunker: Tiny));
        publisher.PublishRelease(ReleaseOn("beta", 10, "beta-10", new PackageRef("core", "9.0.0", true, [], betaMf)));
        await engine.UpdateAsync(await LoadChannel(content, "beta"));
        Assert.That(state.Read().Channel, Is.EqualTo("beta"));

        // Switch to live at a LOWER seq + lower version — a deliberate channel switch, not a downgrade.
        var liveMf = publisher.PublishPackage(Packager.Pack(liveDir, "core", "1.0.0", chunker: Tiny));
        publisher.PublishRelease(ReleaseOn("live", 3, "live-3", new PackageRef("core", "1.0.0", true, [], liveMf)));
        var result = await engine.UpdateAsync(await LoadChannel(content, "live"));

        Assert.That(result.Outcome, Is.EqualTo(UpdateOutcome.Updated));
        Assert.That(state.Read().Channel, Is.EqualTo("live"));
        Assert.That(TreeFixture.TreesEqual(ReadInstalledTree(paths), live), Is.True);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private ReleaseManifest Release(long seq, string id, params PackageRef[] pkgs) => ReleaseOn("beta", seq, id, pkgs);

    private ReleaseManifest ReleaseOn(string channel, long seq, string id, params PackageRef[] pkgs)
        => ManifestTrust.Sign(
            new ReleaseManifest(1, channel, seq, id, "2026-06-04T00:00:00Z", pkgs.ToList()), KeyId, _priv);

    private Task<ReleaseManifest> LoadAsync(IContentSource content) => LoadChannel(content, "beta");

    private async Task<ReleaseManifest> LoadChannel(IContentSource content, string channel)
    {
        var bytes = await content.GetBytesAsync(CdnKeys.Release(channel));
        return ManifestTrust.LoadVerifiedRelease(bytes, _keys); // verifies signature against the pinned key
    }

    private string WriteTree(string name, IReadOnlyDictionary<string, byte[]> tree)
    {
        var dir = Path.Combine(_work, name);
        foreach (var (rel, bytes) in tree)
        {
            var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        return dir;
    }

    private static Dictionary<string, byte[]> ReadInstalledTree(InstallPaths paths)
    {
        var appDir = Path.Combine(paths.Root, "app");
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(appDir))
            return tree;
        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
            tree[Path.GetRelativePath(appDir, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        return tree;
    }

    /// <summary>Fails each chunk's first fetch with a transient network error to exercise retry/backoff.</summary>
    private sealed class FlakyContentSource(IContentSource inner) : IContentSource
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _failedOnce = new(StringComparer.Ordinal);
        private int _injected;
        public int Injected => _injected;

        public async Task<byte[]> GetBytesAsync(string key, CancellationToken ct = default)
        {
            if (CdnKeys.IsChunk(key) && _failedOnce.TryAdd(key, true))
            {
                Interlocked.Increment(ref _injected);
                throw new System.Net.Http.HttpRequestException("simulated transient network blip");
            }
            return await inner.GetBytesAsync(key, ct);
        }

        public Task DownloadToFileAsync(
            string key, string destinationPath, string? expectedSha256 = null,
            IProgress<ContentProgress>? progress = null, CancellationToken ct = default)
            => inner.DownloadToFileAsync(key, destinationPath, expectedSha256, progress, ct);
    }

    /// <summary>Wraps a content source to tally the chunk bytes pulled over the wire (vs reused from disk).</summary>
    private sealed class CountingContentSource(IContentSource inner) : IContentSource
    {
        public long ChunkBytes { get; private set; }
        public int ChunkGets { get; private set; }

        public void Reset() { ChunkBytes = 0; ChunkGets = 0; }

        public async Task<byte[]> GetBytesAsync(string key, CancellationToken ct = default)
        {
            var bytes = await inner.GetBytesAsync(key, ct);
            if (CdnKeys.IsChunk(key)) { ChunkBytes += bytes.Length; ChunkGets++; }
            return bytes;
        }

        public Task DownloadToFileAsync(
            string key, string destinationPath, string? expectedSha256 = null,
            IProgress<ContentProgress>? progress = null, CancellationToken ct = default)
            => inner.DownloadToFileAsync(key, destinationPath, expectedSha256, progress, ct);
    }
}
