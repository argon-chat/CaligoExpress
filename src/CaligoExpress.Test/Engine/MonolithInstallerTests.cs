namespace CaligoExpress.Test.Engine;

using CaligoExpress.Crypto;
using CaligoExpress.Engine;
using CaligoExpress.Manifests;
using CaligoExpress.State;
using CaligoExpress.Test.Support;
using CaligoExpress.Transaction;

/// <summary>
/// Covers the macOS whole-app install path: download → ditto-extract → atomic bundle swap → state write,
/// plus the "already installed" short-circuit and bundle replacement across versions. Crash-safety of the
/// underlying <see cref="TxnOpKind.ReplaceTree"/> op is covered by <see cref="ReplaceTreeRecoveryTests"/>.
/// </summary>
[TestFixture]
public class MonolithInstallerTests
{
    private const string BundleName = "ArgonChat.app";
    private const string LaunchBin = "app/ArgonChat.app";

    private string _root = "";
    private InstallPaths _paths = null!;
    private StateStore _store = null!;
    private InMemoryContentSource _content = null!;
    private FakeArchiveExtractor _extractor = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "caligo-mono-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new InstallPaths(_root, bootstrapperFileName: BundleName);
        _paths.EnsureControlDirs();
        _store = new StateStore(_paths);
        _content = new InMemoryContentSource();
        _extractor = new FakeArchiveExtractor();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task FreshInstall_DownloadsExtractsSwaps_WritesState()
    {
        var release = StageRelease(seq: 1, version: "1.0.0", bundle: BundleTree("Info.plist v1", "MacOS/ArgonChat exec v1"));

        var outcome = await NewInstaller().UpdateAsync(release);

        Assert.That(outcome, Is.EqualTo(UpdateOutcome.Updated));
        var bundle = Path.Combine(_root, "app", BundleName);
        Assert.That(File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")), Is.EqualTo("Info.plist v1"));
        Assert.That(File.Exists(Path.Combine(bundle, "Contents", "MacOS", "ArgonChat")), Is.True);

        var state = _store.Read();
        Assert.That(state.ReleaseId, Is.EqualTo("rel-1"));
        Assert.That(state.ReleaseSeq, Is.EqualTo(1));
        Assert.That(state.Packages[MonolithInstaller.MacAppPackage].Version, Is.EqualTo("1.0.0"));
        Assert.That(Directory.Exists(_paths.TxnDir(GetTxnDirName())), Is.False, "journal must be cleaned up");
    }

    [Test]
    public async Task SecondRun_SameRelease_IsUpToDate_NoReDownload()
    {
        var release = StageRelease(seq: 1, version: "1.0.0", bundle: BundleTree("Info.plist v1", "exec v1"));
        await NewInstaller().UpdateAsync(release);

        var downloadsAfterFirst = _content.DownloadCalls;
        var extractsAfterFirst = _extractor.ExtractCalls;

        var outcome = await NewInstaller().UpdateAsync(release);

        Assert.That(outcome, Is.EqualTo(UpdateOutcome.UpToDate));
        Assert.That(_content.DownloadCalls, Is.EqualTo(downloadsAfterFirst), "must not re-download an identical app");
        Assert.That(_extractor.ExtractCalls, Is.EqualTo(extractsAfterFirst), "must not re-extract an identical app");
    }

    [Test]
    public async Task NewRelease_ReplacesBundle_RemovesOldFiles()
    {
        var v1 = StageRelease(seq: 1, version: "1.0.0", bundle: BundleTree("Info.plist v1", "exec v1", extra: "Contents/Resources/old.txt"));
        await NewInstaller().UpdateAsync(v1);

        var v2 = StageRelease(seq: 2, version: "2.0.0", bundle: BundleTree("Info.plist v2", "exec v2"));
        var outcome = await NewInstaller().UpdateAsync(v2);

        Assert.That(outcome, Is.EqualTo(UpdateOutcome.Updated));
        var bundle = Path.Combine(_root, "app", BundleName);
        Assert.That(File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")), Is.EqualTo("Info.plist v2"));
        Assert.That(File.Exists(Path.Combine(bundle, "Contents", "Resources", "old.txt")), Is.False,
            "a whole-bundle swap must drop files removed in the new version");
        Assert.That(_store.Read().Packages[MonolithInstaller.MacAppPackage].Version, Is.EqualTo("2.0.0"));
    }

    [Test]
    public void Downgrade_Blocked_WithoutAllowRollback()
    {
        var v2 = StageRelease(seq: 2, version: "2.0.0", bundle: BundleTree("v2", "exec v2"));
        NewInstaller().UpdateAsync(v2).GetAwaiter().GetResult();

        var v1 = StageRelease(seq: 1, version: "1.0.0", bundle: BundleTree("v1", "exec v1"));
        Assert.ThrowsAsync<CaligoExpress.Abstractions.CaligoException>(
            async () => await NewInstaller().UpdateAsync(v1));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private MonolithInstaller NewInstaller()
        => new(_paths, _content, _store, new RecoveryService(_paths, _store), _extractor);

    /// <summary>Builds a fake bundle tree (relative paths rooted at the bundle name).</summary>
    private static Dictionary<string, byte[]> BundleTree(string infoPlist, string exec, string? extra = null)
    {
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{BundleName}/Contents/Info.plist"] = System.Text.Encoding.UTF8.GetBytes(infoPlist),
            [$"{BundleName}/Contents/MacOS/ArgonChat"] = System.Text.Encoding.UTF8.GetBytes(exec),
        };
        if (extra is not null)
            tree[$"{BundleName}/{extra}"] = System.Text.Encoding.UTF8.GetBytes("old resource");
        return tree;
    }

    /// <summary>Registers an artifact in the content source, points the extractor at the matching tree, and
    /// returns a release manifest referencing it. The artifact bytes are arbitrary (the fake extractor does
    /// not read them) but must hash to the manifest's declared hash so the download verifies.</summary>
    private ReleaseManifest StageRelease(long seq, string version, Dictionary<string, byte[]> bundle)
    {
        // Distinct artifact bytes per version so the hash differs and the up-to-date check is meaningful.
        var artifactBytes = System.Text.Encoding.UTF8.GetBytes($"zip-{version}-{seq}");
        var hash = Sha256Hasher.HashHex(artifactBytes);
        var key = $"macapp/{version}/ArgonChat.zip";

        _content.Map[key] = artifactBytes;
        _extractor.Tree = bundle;

        return new ReleaseManifest(
            SchemaVersion: 1,
            Channel: "live",
            ReleaseSeq: seq,
            ReleaseId: $"rel-{seq}",
            CreatedAt: "2026-06-11T00:00:00Z",
            Packages: [],
            Launch: null,
            Bootstrapper: null,
            Signature: null,
            MacApp: new MonolithRef(new ArtifactRef(key, hash, artifactBytes.Length), BundleName, version),
            MacLaunch: new LaunchSpec(LaunchBin, []));
    }

    private string GetTxnDirName()
    {
        var journalDir = _paths.JournalDir;
        return Directory.Exists(journalDir) && Directory.GetDirectories(journalDir).Length > 0
            ? Path.GetFileName(Directory.GetDirectories(journalDir)[0])
            : "none";
    }
}
