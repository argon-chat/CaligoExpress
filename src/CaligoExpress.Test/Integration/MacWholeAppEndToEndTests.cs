namespace CaligoExpress.Test.Integration;

using System.IO.Compression;
using CaligoExpress.Abstractions;
using CaligoExpress.Crypto;
using CaligoExpress.Engine;
using CaligoExpress.Manifests;
using CaligoExpress.Publishing;
using CaligoExpress.State;
using CaligoExpress.Test.Support;
using CaligoExpress.Transaction;

/// <summary>
/// The macOS dogfood: publish a whole-app bundle archive + signed mac release to a local "CDN", then run
/// the real <see cref="MonolithInstaller"/> against it — download (hash-verified), extract, atomic bundle
/// swap, state write — and assert the installed <c>.app</c> matches the published one, that an unchanged
/// re-run is a no-op, and that a new version replaces the bundle. Extraction uses a plain-zip stand-in (CI
/// has no <c>ditto</c>); Apple metadata preservation is validated on mac CI per the plan.
/// </summary>
[TestFixture]
public class MacWholeAppEndToEndTests
{
    private const string KeyId = "k1";
    private const string BundleName = "ArgonChat.app";
    private const string LaunchBin = "app/ArgonChat.app";

    private string _work = "";
    private string _cdn = "";
    private string _install = "";
    private byte[] _priv = [];
    private Dictionary<string, byte[]> _keys = new();

    [SetUp]
    public void SetUp()
    {
        _work = Path.Combine(Path.GetTempPath(), "caligo-mac-e2e-" + Guid.NewGuid().ToString("N"));
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
    public async Task Publish_Install_Update_WholeAppBundle()
    {
        var publisher = new CdnPublisher(_cdn);
        var paths = new InstallPaths(_install, bootstrapperFileName: BundleName);
        var state = new StateStore(paths);
        var content = new CaligoExpress.Content.LocalContentSource(_cdn);
        var extractor = new ZipArchiveExtractor();

        MonolithInstaller Installer() => new(paths, content, state, new RecoveryService(paths, state), extractor);

        // ── Publish v1 and install it ────────────────────────────────────────────
        var v1 = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{BundleName}/Contents/Info.plist"] = Bytes("plist v1"),
            [$"{BundleName}/Contents/MacOS/ArgonChat"] = Bytes("exec v1"),
            [$"{BundleName}/Contents/Resources/gone.txt"] = Bytes("removed in v2"),
        };
        PublishMacRelease(publisher, seq: 1, version: "1.0.0", bundle: v1);

        var r1 = await LoadRelease(content);
        Assert.That(await Installer().UpdateAsync(r1), Is.EqualTo(UpdateOutcome.Updated));
        AssertBundle(v1);
        Assert.That(state.Read().Packages[MonolithInstaller.MacAppPackage].Version, Is.EqualTo("1.0.0"));

        // Re-running the same release is a no-op.
        Assert.That(await Installer().UpdateAsync(await LoadRelease(content)), Is.EqualTo(UpdateOutcome.UpToDate));

        // ── Publish v2 and update — bundle is swapped wholesale, removed files gone ─
        var v2 = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{BundleName}/Contents/Info.plist"] = Bytes("plist v2"),
            [$"{BundleName}/Contents/MacOS/ArgonChat"] = Bytes("exec v2"),
        };
        PublishMacRelease(publisher, seq: 2, version: "2.0.0", bundle: v2);

        Assert.That(await Installer().UpdateAsync(await LoadRelease(content)), Is.EqualTo(UpdateOutcome.Updated));
        AssertBundle(v2);
        Assert.That(File.Exists(Path.Combine(_install, "app", BundleName, "Contents", "Resources", "gone.txt")),
            Is.False, "whole-bundle swap must drop files removed in the new version");
        Assert.That(state.Read().ReleaseId, Is.EqualTo("rel-2"));
    }

    [Test]
    public void Install_RejectsTamperedArtifact()
    {
        var publisher = new CdnPublisher(_cdn);
        var paths = new InstallPaths(_install, bootstrapperFileName: BundleName);
        var state = new StateStore(paths);
        var content = new CaligoExpress.Content.LocalContentSource(_cdn);

        var bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{BundleName}/Contents/Info.plist"] = Bytes("plist"),
        };
        PublishMacRelease(publisher, seq: 1, version: "1.0.0", bundle: bundle);

        // Corrupt the published artifact so its bytes no longer match the signed hash.
        var artifactPath = Path.Combine(_cdn, CdnKeys.MacApp("1.0.0").Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(artifactPath, Bytes("tampered"));

        var installer = new MonolithInstaller(paths, content, state, new RecoveryService(paths, state), new ZipArchiveExtractor());
        Assert.ThrowsAsync<HashMismatchException>(async () => await installer.UpdateAsync(
            await LoadRelease(content)));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private void PublishMacRelease(CdnPublisher publisher, long seq, string version, Dictionary<string, byte[]> bundle)
    {
        var zip = ZipBundle(bundle);
        var artifact = publisher.PublishMacApp(version, zip);
        var release = ManifestTrust.Sign(
            new ReleaseManifest(
                SchemaVersion: 1, Channel: "live", ReleaseSeq: seq, ReleaseId: $"rel-{seq}",
                CreatedAt: "2026-06-11T00:00:00Z", Packages: [],
                MacApp: new MonolithRef(artifact, BundleName, version),
                MacLaunch: new LaunchSpec(LaunchBin, [])),
            KeyId, _priv);
        publisher.PublishRelease(release);
    }

    private async Task<ReleaseManifest> LoadRelease(CaligoExpress.Content.LocalContentSource content)
    {
        var bytes = await content.GetBytesAsync(CdnKeys.Release("live"));
        return ManifestTrust.LoadVerifiedRelease(bytes, _keys);
    }

    private void AssertBundle(Dictionary<string, byte[]> expected)
    {
        foreach (var (rel, bytes) in expected)
        {
            var full = Path.Combine(_install, "app", rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.That(File.Exists(full), Is.True, $"missing {rel}");
            Assert.That(File.ReadAllBytes(full), Is.EqualTo(bytes), $"content mismatch {rel}");
        }
    }

    private static byte[] ZipBundle(Dictionary<string, byte[]> tree)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (rel, bytes) in tree)
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                using var es = entry.Open();
                es.Write(bytes);
            }
        return ms.ToArray();
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
