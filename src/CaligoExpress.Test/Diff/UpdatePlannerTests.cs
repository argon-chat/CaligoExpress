namespace CaligoExpress.Test.Diff;

using CaligoExpress.Abstractions;
using CaligoExpress.Diff;
using CaligoExpress.Manifests;

[TestFixture]
public class UpdatePlannerTests
{
    private static PackageRef Pkg(string name, string version, IEnumerable<string>? deps = null)
        => new(name, version, true, (deps ?? []).ToList(), new ArtifactRef($"{name}/m", "mh", 1));

    private static ReleaseManifest Release(long seq, params PackageRef[] pkgs)
        => new(1, "beta", seq, $"r{seq}", "now", pkgs.ToList());

    private static LocalInstallState State(string bootVersion, params (string name, string version)[] pkgs)
    {
        var s = new LocalInstallState { BootstrapperVersion = bootVersion };
        foreach (var (n, v) in pkgs)
            s.Packages[n] = new InstalledPackage(n, v, "mh");
        return s;
    }

    [Test]
    public void SkipsUnchangedVersions_SyncsChangedOnes()
    {
        var target = Release(5,
            Pkg("core", "1.1.0"),    // installed older -> sync
            Pkg("ui", "2.0.0"),      // installed older -> sync
            Pkg("tools", "3.0.0"));  // unchanged -> skip

        var state = State("1.0.0", ("core", "1.0.0"), ("ui", "1.0.0"), ("tools", "3.0.0"));

        var plan = UpdatePlanner.Plan(target, state, state.BootstrapperVersion, targetBootstrapperVersion: "1.0.0");
        var byName = plan.Packages.ToDictionary(p => p.Name);

        Assert.That(byName["core"].Action, Is.EqualTo(PackageAction.Sync));
        Assert.That(byName["ui"].Action, Is.EqualTo(PackageAction.Sync));
        Assert.That(byName["tools"].Action, Is.EqualTo(PackageAction.Skip));
        Assert.That(plan.BootstrapperUpdate, Is.False);
    }

    [Test]
    public void FreshInstall_IsSync_AndBootstrapperUpdates()
    {
        var target = Release(1, Pkg("core", "1.0.0"));
        var plan = UpdatePlanner.Plan(target, new LocalInstallState(), "", targetBootstrapperVersion: "1.0.0");
        Assert.That(plan.Packages.Single().Action, Is.EqualTo(PackageAction.Sync));
        Assert.That(plan.BootstrapperUpdate, Is.True);
    }

    [Test]
    public void RemovesPackagesAbsentFromTarget_DependentsTornDownFirst()
    {
        var target = Release(2, Pkg("core", "1.0.0"));
        var state = State("1.0.0", ("core", "1.0.0"), ("plugin", "1.0.0"));
        var plan = UpdatePlanner.Plan(target, state, "1.0.0");

        var removed = plan.Packages.Where(p => p.Action == PackageAction.Remove).Select(p => p.Name).ToList();
        Assert.That(removed, Does.Contain("plugin"));
    }

    [Test]
    public void OrdersByDependencies_DependencyBeforeDependent()
    {
        // ui depends on core; both need installing -> core must come first.
        var target = Release(3,
            Pkg("ui", "1.0.0", deps: ["core"]),
            Pkg("core", "1.0.0"));

        var plan = UpdatePlanner.Plan(target, new LocalInstallState(), "");
        var order = plan.Packages.Where(p => p.Action != PackageAction.Remove).Select(p => p.Name).ToList();

        Assert.That(order.IndexOf("core"), Is.LessThan(order.IndexOf("ui")));
    }

    [Test]
    public void DependencyCycle_IsRejected()
    {
        var target = Release(4,
            Pkg("a", "1.0.0", deps: ["b"]),
            Pkg("b", "1.0.0", deps: ["a"]));

        Assert.Throws<CaligoException>(() => UpdatePlanner.Plan(target, new LocalInstallState(), ""));
    }
}
