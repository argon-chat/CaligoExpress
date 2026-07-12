namespace CaligoExpress.Test.Engine;

using CaligoExpress.Abstractions;
using CaligoExpress.Content;
using CaligoExpress.Crypto;
using CaligoExpress.Engine;
using CaligoExpress.Locking;
using CaligoExpress.Manifests;
using CaligoExpress.Publishing;
using CaligoExpress.State;
using CaligoExpress.Transaction;

[TestFixture]
public class BootstrapAndLockTests
{
    private string _root = "";
    private string _cdn = "";

    [SetUp]
    public void SetUp()
    {
        var work = Path.Combine(Path.GetTempPath(), "caligo-boot-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(work, "install");
        _cdn = Path.Combine(work, "cdn");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_cdn);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(Directory.GetParent(_root)!.FullName, recursive: true); } catch { }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessStartSpec> Started { get; } = [];
        public int Start(ProcessStartSpec spec) { Started.Add(spec); return 4242; }
    }

    [Test]
    public void SelfUpdater_Swap_PromotesNewExeAndKeepsOld()
    {
        var paths = new InstallPaths(_root);
        paths.EnsureControlDirs();
        File.WriteAllText(paths.BootstrapperExe, "OLD");
        File.WriteAllText(paths.BootstrapperNew, "NEW");

        new SelfUpdater(paths, new LocalContentSource(_cdn)).Swap();

        Assert.That(File.ReadAllText(paths.BootstrapperExe), Is.EqualTo("NEW"));
        Assert.That(File.ReadAllText(paths.BootstrapperOld), Is.EqualTo("OLD"));
        Assert.That(File.Exists(paths.BootstrapperNew), Is.False);
    }

    [Test]
    public void ResolveStagedBundle_PrefersExpectedName_WhenPresent()
    {
        var stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "ArgonChat.app"));
        Directory.CreateDirectory(Path.Combine(stage, "Other.app"));

        var resolved = SelfUpdater.ResolveStagedBundle(stage, "ArgonChat.app");

        Assert.That(resolved, Is.EqualTo(Path.Combine(stage, "ArgonChat.app")));
    }

    [Test]
    public void ResolveStagedBundle_FallsBackToLoneApp_WhenNameDiffers()
    {
        // The mac publish pipeline names the bundle from <ApplicationTitle> ("Argon.Installer.app"), which
        // does not match the expected "ArgonChat.app" — the lone .app must still be found.
        var stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "Argon.Installer.app"));

        var resolved = SelfUpdater.ResolveStagedBundle(stage, "ArgonChat.app");

        Assert.That(resolved, Is.EqualTo(Path.Combine(stage, "Argon.Installer.app")));
    }

    [Test]
    public void ResolveStagedBundle_Throws_WhenNoApp()
    {
        var stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(stage);

        Assert.Throws<CaligoException>(() => SelfUpdater.ResolveStagedBundle(stage, "ArgonChat.app"));
    }

    [Test]
    public void ResolveStagedBundle_Throws_WhenAmbiguous()
    {
        var stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "A.app"));
        Directory.CreateDirectory(Path.Combine(stage, "B.app"));

        Assert.Throws<CaligoException>(() => SelfUpdater.ResolveStagedBundle(stage, "ArgonChat.app"));
    }

    [Test]
    public void InstallLock_IsExclusive_AndReentrantAfterRelease()
    {
        var paths = new InstallPaths(_root);
        paths.EnsureControlDirs();

        var first = InstallLock.Acquire(paths, TimeSpan.FromSeconds(1));
        Assert.Throws<CaligoException>(() => InstallLock.Acquire(paths, TimeSpan.FromMilliseconds(50)));

        first.Dispose();
        Assert.DoesNotThrow(() =>
        {
            using var again = InstallLock.Acquire(paths, TimeSpan.FromSeconds(1));
        });
    }

    [Test]
    public async Task BootstrapFlow_SelfUpdatesFirst_ThenProceedsOnRelaunch()
    {
        var paths = new InstallPaths(_root);
        paths.EnsureControlDirs();
        File.WriteAllText(paths.BootstrapperExe, "boot-1.0.0");

        var state = new StateStore(paths);
        await state.WriteAsync(new LocalInstallState { ReleaseId = "rel-1", ReleaseSeq = 1, BootstrapperVersion = "1.0.0" });

        // Publish a new bootstrapper artifact + its separate pointer manifest.
        var newBytes = System.Text.Encoding.UTF8.GetBytes("boot-2.0.0");
        var bootArtifact = new CdnPublisher(_cdn).PublishBootstrapper("2.0.0", newBytes);
        var boot = new BootstrapperManifest(1, "beta", 1, "2.0.0", "now", bootArtifact);
        var release = new ReleaseManifest(1, "beta", 2, "rel-2", "now", []); // no packages: isolate the self-update path

        var content = new LocalContentSource(_cdn);
        var recovery = new RecoveryService(paths, state);
        var selfUpdater = new SelfUpdater(paths, content);
        var runner = new FakeRunner();
        var flow = new BootstrapFlow(paths, content, state, recovery, selfUpdater, runner);

        // First run: must self-update and relaunch.
        var first = await flow.RunAsync(release, boot, relaunchArgs: ["--resumed"]);
        Assert.That(first, Is.EqualTo(BootstrapOutcome.SelfUpdateRelaunching));
        Assert.That(File.ReadAllText(paths.BootstrapperExe), Is.EqualTo("boot-2.0.0"), "new bootstrapper is in place");
        Assert.That(state.Read().BootstrapperVersion, Is.EqualTo("2.0.0"), "loop-guard: version advanced before relaunch");
        Assert.That(runner.Started, Has.Count.EqualTo(1));
        Assert.That(runner.Started[0].Arguments, Is.EqualTo(new[] { "--resumed" }));

        // Second run (the relaunched process): no more self-update, and the stale .old is cleaned up.
        var second = await flow.RunAsync(release, boot, relaunchArgs: ["--resumed"]);
        Assert.That(second, Is.EqualTo(BootstrapOutcome.UpToDate));
        Assert.That(runner.Started, Has.Count.EqualTo(1), "must not relaunch again");
        Assert.That(File.Exists(paths.BootstrapperOld), Is.False, "old bootstrapper backup cleaned up");
    }

    [Test]
    public void NeedsBootstrapperUpdate_BlocksDowngrade_AllowsForward()
    {
        Assert.That(BootstrapFlow.NeedsBootstrapperUpdate("1.0.0", "1.0.0"), Is.False);
        Assert.That(BootstrapFlow.NeedsBootstrapperUpdate("1.0.0", "1.1.0"), Is.True);
        Assert.That(BootstrapFlow.NeedsBootstrapperUpdate("2.0.0", "1.0.0"), Is.False, "never self-downgrade");
    }
}
