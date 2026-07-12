namespace CaligoExpress.Test.Transaction;

using CaligoExpress.Manifests;
using CaligoExpress.State;
using CaligoExpress.Transaction;

/// <summary>
/// Crash-safety of the directory-level <see cref="TxnOpKind.ReplaceTree"/> op that backs the macOS
/// whole-app bundle swap: a crash at any point must leave the live bundle wholly old or wholly new — never
/// a half-swapped mix — and recovery must agree with the persisted state.
/// </summary>
[TestFixture]
public class ReplaceTreeRecoveryTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "caligo-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Commit_SwapsBundle_WritesState_CleansUp()
    {
        var (paths, store, _, newTree, journal) = Arrange(hasOldBundle: true);

        await new TransactionRunner(paths, store).CommitAsync(journal);

        Assert.That(TreesEqual(ReadBundle(paths), newTree), Is.True);
        Assert.That(store.Read().ReleaseId, Is.EqualTo("new"));
        Assert.That(Directory.Exists(paths.TxnDir(journal.Id)), Is.False);
    }

    [Test]
    public void Crash_BeforeStateWrite_RollsBackToOldBundle()
    {
        var (paths, store, oldTree, _, journal) = Arrange(hasOldBundle: true);
        var runner = new TransactionRunner(paths, store)
        {
            CrashHook = label => { if (label == "before-state-write") throw new SimulatedCrash(); }
        };

        Assert.ThrowsAsync<SimulatedCrash>(async () => await runner.CommitAsync(journal));
        var report = new RecoveryService(paths, store).RecoverPending();

        Assert.That(report.RolledBack, Is.EqualTo(1));
        Assert.That(TreesEqual(ReadBundle(paths), oldTree), Is.True, "must restore the old bundle exactly");
        Assert.That(store.Read().ReleaseId, Is.EqualTo("old"));
        Assert.That(Directory.Exists(paths.TxnDir(journal.Id)), Is.False);
    }

    [Test]
    public void Crash_FirstInstall_BeforeStateWrite_RemovesPartialBundle()
    {
        var (paths, store, _, _, journal) = Arrange(hasOldBundle: false);
        var runner = new TransactionRunner(paths, store)
        {
            CrashHook = label => { if (label == "before-state-write") throw new SimulatedCrash(); }
        };

        Assert.ThrowsAsync<SimulatedCrash>(async () => await runner.CommitAsync(journal));
        new RecoveryService(paths, store).RecoverPending();

        Assert.That(Directory.Exists(Path.Combine(paths.Root, "app", "ArgonChat.app")), Is.False,
            "a first install with no prior bundle must be removed on rollback");
        Assert.That(store.Read().ReleaseId, Is.EqualTo("old"));
    }

    [Test]
    public async Task Crash_AtEveryBoundary_AlwaysLandsConsistent()
    {
        var labels = new[] { "after-committing-status", "after-op", "before-state-write", "after-state-write" };

        foreach (var crashAt in labels)
        {
            TearDown();
            SetUp();
            var (paths, store, oldTree, newTree, journal) = Arrange(hasOldBundle: true);
            var crashed = false;
            var runner = new TransactionRunner(paths, store)
            {
                CrashHook = label =>
                {
                    if (label.StartsWith(crashAt) && !crashed) { crashed = true; throw new SimulatedCrash(); }
                }
            };

            try { await runner.CommitAsync(journal); } catch (SimulatedCrash) { }
            new RecoveryService(paths, store).RecoverPending();

            var tree = ReadBundle(paths);
            var isOld = TreesEqual(tree, oldTree);
            var isNew = TreesEqual(tree, newTree);
            Assert.That(isOld || isNew, Is.True, $"crash@{crashAt}: bundle must be wholly old or wholly new");
            if (store.Read().ReleaseId == "new")
                Assert.That(isNew, Is.True, $"crash@{crashAt}: state says new but bundle differs");
            else
                Assert.That(isOld, Is.True, $"crash@{crashAt}: state says old but bundle differs");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private sealed class SimulatedCrash : Exception;

    private (InstallPaths, StateStore, Dictionary<string, byte[]> oldTree, Dictionary<string, byte[]> newTree, TransactionJournal)
        Arrange(bool hasOldBundle)
    {
        var paths = new InstallPaths(_root, bootstrapperFileName: "ArgonChat.app");
        paths.EnsureControlDirs();

        var oldTree = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Contents/Info.plist"] = Bytes("old plist"),
            ["Contents/MacOS/ArgonChat"] = Bytes("old exec"),
            ["Contents/Resources/gone.txt"] = Bytes("removed next version"),
        };
        var newTree = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Contents/Info.plist"] = Bytes("new plist"),
            ["Contents/MacOS/ArgonChat"] = Bytes("new exec"),
        };

        var liveBundle = Path.Combine(paths.Root, "app", "ArgonChat.app");
        if (hasOldBundle)
            WriteTree(liveBundle, oldTree);

        var store = new StateStore(paths);
        store.WriteAsync(new LocalInstallState { ReleaseSeq = 1, ReleaseId = "old" }).GetAwaiter().GetResult();

        // Stage the new bundle as the txn would: fully materialized under the staged dir.
        const string txnId = "txn-tree";
        var staging = new StagingArea(paths, txnId);
        var stagedBundle = Path.Combine(staging.StagedDir, "ArgonChat.app");
        WriteTree(stagedBundle, newTree);

        var op = new TxnOp
        {
            Kind = TxnOpKind.ReplaceTree,
            TargetPath = liveBundle,
            StagedPath = stagedBundle,
            BackupPath = staging.BackupPathFor("app"),
        };
        var journal = new TransactionJournal
        {
            Id = txnId,
            TargetReleaseSeq = 2,
            TargetReleaseId = "new",
            Operations = [op],
            NewState = new LocalInstallState { ReleaseSeq = 2, ReleaseId = "new" },
        };
        Journals.Write(paths.TxnJournalFile(txnId), journal);

        return (paths, store, oldTree, newTree, journal);
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static void WriteTree(string root, Dictionary<string, byte[]> tree)
    {
        foreach (var (rel, bytes) in tree)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }

    private static Dictionary<string, byte[]> ReadBundle(InstallPaths paths)
    {
        var bundle = Path.Combine(paths.Root, "app", "ArgonChat.app");
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(bundle))
            return tree;
        foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            tree[Path.GetRelativePath(bundle, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        return tree;
    }

    private static bool TreesEqual(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var other) || !v.AsSpan().SequenceEqual(other))
                return false;
        return true;
    }
}
