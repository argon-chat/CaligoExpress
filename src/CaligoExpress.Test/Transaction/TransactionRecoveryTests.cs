namespace CaligoExpress.Test.Transaction;

using CaligoExpress.Crypto;
using CaligoExpress.Manifests;
using CaligoExpress.State;
using CaligoExpress.Test.Support;
using CaligoExpress.Transaction;

[TestFixture]
public class TransactionRecoveryTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "caligo-txn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Commit_AppliesNewTree_WritesState_CleansUp()
    {
        var (paths, store, oldTree, newTree, journal) = Arrange();

        await new TransactionRunner(paths, store).CommitAsync(journal);

        Assert.That(TreeFixture.TreesEqual(ReadAppTree(paths), newTree), Is.True);
        Assert.That(store.Read().ReleaseId, Is.EqualTo("new"));
        Assert.That(Directory.Exists(paths.TxnDir(journal.Id)), Is.False);
    }

    [Test]
    public async Task Crash_BeforeStateWrite_RollsBackToOldTree()
    {
        var (paths, store, oldTree, _, journal) = Arrange();
        var runner = new TransactionRunner(paths, store)
        {
            CrashHook = label => { if (label == "before-state-write") throw new SimulatedCrash(); }
        };

        Assert.ThrowsAsync<SimulatedCrash>(async () => await runner.CommitAsync(journal));

        var report = new RecoveryService(paths, store).RecoverPending();

        Assert.That(report.RolledBack, Is.EqualTo(1));
        Assert.That(TreeFixture.TreesEqual(ReadAppTree(paths), oldTree), Is.True, "must restore the old tree exactly");
        Assert.That(store.Read().ReleaseId, Is.EqualTo("old"), "state must still point at the old release");
        Assert.That(Directory.Exists(paths.TxnDir(journal.Id)), Is.False);
    }

    [Test]
    public async Task Crash_AfterStateWrite_RollsForwardToNewTree()
    {
        var (paths, store, _, newTree, journal) = Arrange();
        var runner = new TransactionRunner(paths, store)
        {
            CrashHook = label => { if (label == "after-state-write") throw new SimulatedCrash(); }
        };

        Assert.ThrowsAsync<SimulatedCrash>(async () => await runner.CommitAsync(journal));

        var report = new RecoveryService(paths, store).RecoverPending();

        Assert.That(report.Completed, Is.EqualTo(1));
        Assert.That(TreeFixture.TreesEqual(ReadAppTree(paths), newTree), Is.True);
        Assert.That(store.Read().ReleaseId, Is.EqualTo("new"));
        Assert.That(Directory.Exists(paths.TxnDir(journal.Id)), Is.False);
    }

    [Test]
    public async Task Crash_AtEveryOpBoundary_AlwaysLandsConsistent()
    {
        // Re-run the whole scenario crashing after each labelled point; the result must always be a
        // clean old tree (pre-commit) or clean new tree (post-commit), never a mix.
        var labels = new[] { "after-committing-status", "after-op", "before-state-write", "after-state-write" };

        foreach (var crashAt in labels)
        {
            TearDown();
            SetUp();
            var (paths, store, oldTree, newTree, journal) = Arrange();
            var crashed = false;
            var runner = new TransactionRunner(paths, store)
            {
                CrashHook = label =>
                {
                    if (label.StartsWith(crashAt) && !crashed)
                    {
                        crashed = true;
                        throw new SimulatedCrash();
                    }
                }
            };

            try { await runner.CommitAsync(journal); } catch (SimulatedCrash) { }

            new RecoveryService(paths, store).RecoverPending();

            var tree = ReadAppTree(paths);
            var isOld = TreeFixture.TreesEqual(tree, oldTree);
            var isNew = TreeFixture.TreesEqual(tree, newTree);
            Assert.That(isOld || isNew, Is.True, $"crash@{crashAt}: tree must be wholly old or wholly new");

            // State and tree must agree.
            if (store.Read().ReleaseId == "new")
                Assert.That(isNew, Is.True, $"crash@{crashAt}: state says new but tree differs");
            else
                Assert.That(isOld, Is.True, $"crash@{crashAt}: state says old but tree differs");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private sealed class SimulatedCrash : Exception;

    private (InstallPaths, StateStore, Dictionary<string, byte[]> oldTree, Dictionary<string, byte[]> newTree, TransactionJournal)
        Arrange()
    {
        var oldTree = TreeFixture.Tree();
        oldTree["bin/app.dll"] = TreeFixture.Buffer(4096, 1);
        oldTree["bin/keep.dll"] = TreeFixture.Buffer(2048, 2);
        oldTree["data/old.cfg"] = TreeFixture.Bytes("old config");

        var newTree = TreeFixture.Tree();
        newTree["bin/app.dll"] = TreeFixture.Buffer(4096, 1, flipFrom: 1000, flipLen: 100); // replace
        newTree["bin/keep.dll"] = oldTree["bin/keep.dll"];                                   // unchanged
        newTree["data/new.cfg"] = TreeFixture.Bytes("new config");                           // add; old.cfg deleted

        var paths = new InstallPaths(_root);
        paths.EnsureControlDirs();
        foreach (var (rel, bytes) in oldTree)
        {
            var full = paths.ResolveInstalledFile("app", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        var store = new StateStore(paths);
        store.WriteAsync(new LocalInstallState { ReleaseSeq = 1, ReleaseId = "old" }).GetAwaiter().GetResult();

        var journal = BuildTxn(paths, oldTree, newTree);
        return (paths, store, oldTree, newTree, journal);
    }

    private static TransactionJournal BuildTxn(
        InstallPaths paths, Dictionary<string, byte[]> oldTree, Dictionary<string, byte[]> newTree)
    {
        const string txnId = "txn1";
        var staging = new StagingArea(paths, txnId);
        var ops = new List<TxnOp>();
        var token = 0;

        foreach (var rel in oldTree.Keys)
            if (!newTree.ContainsKey(rel))
            {
                var t = (token++).ToString();
                ops.Add(new TxnOp
                {
                    Kind = TxnOpKind.Delete,
                    TargetPath = paths.ResolveInstalledFile("app", rel),
                    BackupPath = staging.BackupPathFor(t),
                    OldHash = Sha256Hasher.HashHex(oldTree[rel]),
                });
            }

        foreach (var (rel, bytes) in newTree)
        {
            if (oldTree.TryGetValue(rel, out var oldBytes) && oldBytes.AsSpan().SequenceEqual(bytes))
                continue; // unchanged -> no op, mirrors the real planner

            var t = (token++).ToString();
            staging.WriteStagedAsync(t, bytes).GetAwaiter().GetResult();
            var target = paths.ResolveInstalledFile("app", rel);

            ops.Add(oldTree.ContainsKey(rel)
                ? new TxnOp
                {
                    Kind = TxnOpKind.Replace,
                    TargetPath = target,
                    StagedPath = staging.StagePathFor(t),
                    BackupPath = staging.BackupPathFor(t),
                    OldHash = Sha256Hasher.HashHex(oldTree[rel]),
                    NewHash = Sha256Hasher.HashHex(bytes),
                }
                : new TxnOp
                {
                    Kind = TxnOpKind.Add,
                    TargetPath = target,
                    StagedPath = staging.StagePathFor(t),
                    NewHash = Sha256Hasher.HashHex(bytes),
                });
        }

        var journal = new TransactionJournal
        {
            Id = txnId,
            TargetReleaseSeq = 2,
            TargetReleaseId = "new",
            Operations = ops,
            NewState = new LocalInstallState { ReleaseSeq = 2, ReleaseId = "new" },
        };
        Journals.Write(paths.TxnJournalFile(txnId), journal);
        return journal;
    }

    private static Dictionary<string, byte[]> ReadAppTree(InstallPaths paths)
    {
        var appDir = Path.Combine(paths.Root, "app");
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(appDir))
            return tree;
        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(appDir, file).Replace('\\', '/');
            tree[rel] = File.ReadAllBytes(file);
        }
        return tree;
    }
}
