namespace CaligoExpress.Diff;

using CaligoExpress.Abstractions;
using CaligoExpress.Manifests;

public enum PackageAction
{
    /// <summary>Installed version already matches the target.</summary>
    Skip,

    /// <summary>Materialize the target version by reusing local chunks and downloading the missing ones.
    /// One code path covers a fresh install (no local chunks → download all) and any update (download the
    /// difference), so there is no separate "full vs delta" decision and no version-gap cliff.</summary>
    Sync,

    /// <summary>Package is no longer in the release; its files must be removed.</summary>
    Remove,
}

/// <summary>One package's planned action, in dependency order.</summary>
public sealed record PackagePlan(
    string Name,
    PackageAction Action,
    PackageRef? Target,
    string? InstalledVersion);

/// <summary>The full set of per-package actions plus whether the bootstrapper itself must update.</summary>
public sealed record UpdatePlan(
    IReadOnlyList<PackagePlan> Packages,
    bool BootstrapperUpdate)
{
    public bool HasWork => BootstrapperUpdate || Packages.Any(p => p.Action != PackageAction.Skip);
    public IEnumerable<PackagePlan> Actionable => Packages.Where(p => p.Action != PackageAction.Skip);
}

/// <summary>
/// Client-side: diffs a target release against the local state and decides, per package, whether to skip
/// (version already installed), sync (chunk-reassemble the target), or remove (gone from the release) —
/// then orders the work by the dependency DAG. Update direction / downgrade protection is enforced by the
/// engine via the release sequence, not here. Which chunks are reused vs downloaded is decided later, at
/// reassembly time, by comparing chunk hashes — not in the plan.
/// </summary>
public static class UpdatePlanner
{
    public static UpdatePlan Plan(
        ReleaseManifest target,
        LocalInstallState current,
        string installedBootstrapperVersion,
        string? targetBootstrapperVersion = null)
    {
        var plans = new List<PackagePlan>();

        foreach (var pkg in target.Packages)
        {
            current.Packages.TryGetValue(pkg.Name, out var installed);
            var installedVersion = installed?.Version;

            var action = installedVersion is not null &&
                         string.Equals(installedVersion, pkg.Version, StringComparison.OrdinalIgnoreCase)
                ? PackageAction.Skip
                : PackageAction.Sync;

            plans.Add(new PackagePlan(pkg.Name, action, pkg, installedVersion));
        }

        // Packages installed but absent from the target are removed.
        var targetNames = target.Packages.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in current.Packages.Keys)
            if (!targetNames.Contains(name))
                plans.Add(new PackagePlan(name, PackageAction.Remove, null, current.Packages[name].Version));

        var ordered = OrderByDependencies(plans, target);

        // The bootstrapper is a separate pointer now; a caller that knows the target bootstrapper version
        // passes it so HasWork reflects a pending self-update too.
        var bootstrapperUpdate = targetBootstrapperVersion is not null && !string.Equals(
            installedBootstrapperVersion, targetBootstrapperVersion, StringComparison.OrdinalIgnoreCase);

        return new UpdatePlan(ordered, bootstrapperUpdate);
    }

    /// <summary>
    /// Topological order so a dependency is acted on before its dependents (removals reversed, so
    /// dependents are torn down first). Cycles are rejected. Ties broken by name for determinism.
    /// </summary>
    private static List<PackagePlan> OrderByDependencies(List<PackagePlan> plans, ReleaseManifest target)
    {
        var depsByName = target.Packages.ToDictionary(p => p.Name, p => p.Deps, StringComparer.Ordinal);
        var planByName = plans.ToDictionary(p => p.Name, StringComparer.Ordinal);

        var visited = new Dictionary<string, int>(StringComparer.Ordinal); // 0=visiting,1=done
        var installOrder = new List<PackagePlan>();

        void Visit(string name)
        {
            if (visited.TryGetValue(name, out var state))
            {
                if (state == 0)
                    throw new CaligoException($"dependency cycle detected at package '{name}'");
                return;
            }
            visited[name] = 0;

            if (depsByName.TryGetValue(name, out var deps))
                foreach (var dep in deps.OrderBy(d => d, StringComparer.Ordinal))
                    if (planByName.ContainsKey(dep))
                        Visit(dep);

            visited[name] = 1;
            if (planByName.TryGetValue(name, out var plan) && plan.Action != PackageAction.Remove)
                installOrder.Add(plan);
        }

        foreach (var name in plans.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
            if (depsByName.ContainsKey(name))
                Visit(name);

        // Removals run first and in reverse dependency order (dependents before dependencies).
        var removals = plans.Where(p => p.Action == PackageAction.Remove)
            .OrderByDescending(p => p.Name, StringComparer.Ordinal)
            .ToList();

        return removals.Concat(installOrder).ToList();
    }
}
