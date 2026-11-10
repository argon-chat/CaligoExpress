//! Diffs a target release against the local state: per package, skip (that version is installed),
//! sync (reassemble the target from local and downloaded chunks: one path for a fresh install and any
//! update) or remove (gone from the release), ordered by the dependency DAG. Which chunks are reused
//! is decided later, at reassembly, by hash.

use std::collections::{BTreeMap, BTreeSet, HashMap};

use crate::manifest::{LocalInstallState, PackageRef, ReleaseManifest};
use crate::{Error, Result};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PackageAction {
    Skip,
    Sync,
    Remove,
}

#[derive(Clone, Debug)]
pub struct PackagePlan {
    pub name: String,
    pub action: PackageAction,
    pub target: Option<PackageRef>,
    pub installed_version: Option<String>,
}

#[derive(Clone, Debug)]
pub struct UpdatePlan {
    pub packages: Vec<PackagePlan>,
    pub bootstrapper_update: bool,
}

impl UpdatePlan {
    pub fn has_work(&self) -> bool {
        self.bootstrapper_update || self.packages.iter().any(|p| p.action != PackageAction::Skip)
    }

    pub fn actionable(&self) -> impl Iterator<Item = &PackagePlan> {
        self.packages.iter().filter(|p| p.action != PackageAction::Skip)
    }
}

/// `target_bootstrapper_version`, when known, makes [`UpdatePlan::has_work`] count a pending
/// bootstrapper self-update.
pub fn plan(
    target: &ReleaseManifest,
    current: &LocalInstallState,
    installed_bootstrapper_version: &str,
    target_bootstrapper_version: Option<&str>,
) -> Result<UpdatePlan> {
    let mut plans: Vec<PackagePlan> = target
        .packages
        .iter()
        .map(|pkg| {
            let installed = current.packages.get(&pkg.name).map(|p| p.version.clone());
            let action = if installed.as_deref().is_some_and(|v| v.eq_ignore_ascii_case(&pkg.version)) {
                PackageAction::Skip
            } else {
                PackageAction::Sync
            };
            PackagePlan { name: pkg.name.clone(), action, target: Some(pkg.clone()), installed_version: installed }
        })
        .collect();

    let target_names: BTreeSet<&str> = target.packages.iter().map(|p| p.name.as_str()).collect();
    for (name, installed) in &current.packages {
        if !target_names.contains(name.as_str()) {
            plans.push(PackagePlan {
                name: name.clone(),
                action: PackageAction::Remove,
                target: None,
                installed_version: Some(installed.version.clone()),
            });
        }
    }

    let packages = order_by_dependencies(plans, target)?;
    let bootstrapper_update =
        target_bootstrapper_version.is_some_and(|t| !t.eq_ignore_ascii_case(installed_bootstrapper_version));
    Ok(UpdatePlan { packages, bootstrapper_update })
}

/// Removals first, dependents before their dependencies (by name, descending, as the .NET client
/// does); then installs in topological order, ties broken by name. A cycle is an error.
fn order_by_dependencies(plans: Vec<PackagePlan>, target: &ReleaseManifest) -> Result<Vec<PackagePlan>> {
    let deps: HashMap<&str, &Vec<String>> = target.packages.iter().map(|p| (p.name.as_str(), &p.deps)).collect();
    let mut by_name: BTreeMap<String, PackagePlan> = BTreeMap::new();
    for plan in plans {
        by_name.insert(plan.name.clone(), plan);
    }

    #[derive(PartialEq)]
    enum Mark {
        Visiting,
        Done,
    }
    fn visit(
        name: &str,
        deps: &HashMap<&str, &Vec<String>>,
        by_name: &BTreeMap<String, PackagePlan>,
        marks: &mut HashMap<String, Mark>,
        order: &mut Vec<String>,
    ) -> Result<()> {
        match marks.get(name) {
            Some(Mark::Visiting) => return Err(Error::other(format!("dependency cycle detected at package '{name}'"))),
            Some(Mark::Done) => return Ok(()),
            None => {}
        }
        marks.insert(name.to_owned(), Mark::Visiting);
        if let Some(ds) = deps.get(name) {
            let mut ds: Vec<&String> = ds.iter().collect();
            ds.sort();
            for dep in ds {
                if by_name.contains_key(dep.as_str()) {
                    visit(dep, deps, by_name, marks, order)?;
                }
            }
        }
        marks.insert(name.to_owned(), Mark::Done);
        if by_name.get(name).is_some_and(|p| p.action != PackageAction::Remove) {
            order.push(name.to_owned());
        }
        Ok(())
    }

    let mut marks = HashMap::new();
    let mut install_order = Vec::new();
    for name in by_name.keys() {
        if deps.contains_key(name.as_str()) {
            visit(name, &deps, &by_name, &mut marks, &mut install_order)?;
        }
    }

    let mut out: Vec<PackagePlan> = by_name
        .values()
        .rev()
        .filter(|p| p.action == PackageAction::Remove)
        .cloned()
        .collect();
    out.extend(install_order.iter().map(|n| by_name[n].clone()));
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::manifest::{ArtifactRef, InstalledPackage};

    fn pkg(name: &str, version: &str, deps: &[&str]) -> PackageRef {
        PackageRef {
            name: name.into(),
            version: version.into(),
            required: true,
            deps: deps.iter().map(|d| d.to_string()).collect(),
            manifest: ArtifactRef { key: String::new(), hash: String::new(), size: 0 },
        }
    }

    fn release(packages: Vec<PackageRef>) -> ReleaseManifest {
        serde_json::from_value(serde_json::json!({
            "schemaVersion": 1, "channel": "live", "releaseSeq": 2, "releaseId": "r2",
            "createdAt": "", "packages": packages,
        }))
        .unwrap()
    }

    fn installed(name: &str, version: &str) -> (String, InstalledPackage) {
        (name.into(), InstalledPackage { name: name.into(), version: version.into(), manifest_hash: String::new(), manifest: None })
    }

    #[test]
    fn dependencies_install_first_removals_lead_and_installed_versions_are_skipped() {
        let target = release(vec![pkg("app", "2", &["core", "assets"]), pkg("core", "1", &[]), pkg("assets", "3", &["core"])]);
        let current = LocalInstallState {
            packages: [installed("core", "1"), installed("old", "1"), installed("zold", "1")].into_iter().collect(),
            ..Default::default()
        };
        let plan = plan(&target, &current, "1.0.0", Some("1.0.0")).unwrap();
        let order: Vec<_> = plan.packages.iter().map(|p| (p.name.as_str(), p.action)).collect();
        assert_eq!(
            order,
            [
                ("zold", PackageAction::Remove),
                ("old", PackageAction::Remove),
                ("core", PackageAction::Skip),
                ("assets", PackageAction::Sync),
                ("app", PackageAction::Sync),
            ]
        );
        assert!(plan.has_work());
        assert!(!plan.bootstrapper_update);
    }

    #[test]
    fn a_dependency_cycle_is_rejected() {
        let target = release(vec![pkg("a", "1", &["b"]), pkg("b", "1", &["a"])]);
        assert!(plan(&target, &LocalInstallState::default(), "", None).is_err());
    }
}
