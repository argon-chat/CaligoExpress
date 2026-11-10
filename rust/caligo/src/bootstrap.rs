//! What the bootstrapper runs before it starts the app: take the install lock, recover any
//! interrupted transaction, clean up after a previous self-update, update itself first when its
//! channel says so (then relaunch), and otherwise bring the app up to date.

use std::time::Duration;

use crate::content::ContentSource;
use crate::keys::KeyScheme;
use crate::lock::InstallLock;
use crate::manifest::{BootstrapperManifest, ReleaseManifest};
use crate::monolith::{ArchiveExtractor, Ditto, MonolithInstaller, is_installed};
use crate::paths::InstallPaths;
use crate::planner::plan;
use crate::self_update::SelfUpdater;
use crate::state::StateStore;
use crate::txn::recover_pending;
use crate::update::{UpdateEngine, UpdateOptions, UpdateOutcome};
use crate::version::PackageVersion;
use crate::{Cancel, Error, IS_MAC, Progress, Result};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BootstrapOutcome {
    /// The bootstrapper replaced and relaunched itself: this process must exit now.
    SelfUpdateRelaunching,
    Updated,
    UpToDate,
}

#[derive(Clone, Debug)]
pub struct BootstrapOptions {
    pub update: UpdateOptions,
    pub lock_timeout: Duration,
}

impl Default for BootstrapOptions {
    fn default() -> Self {
        Self { update: UpdateOptions::default(), lock_timeout: Duration::from_secs(30) }
    }
}

pub struct BootstrapFlow<'a> {
    pub paths: &'a InstallPaths,
    pub content: &'a dyn ContentSource,
    pub keys: &'a KeyScheme,
    /// For the macOS bundles; `ditto` when `None`.
    pub extractor: Option<&'a dyn ArchiveExtractor>,
}

impl BootstrapFlow<'_> {
    /// `release` and `bootstrapper` must already be verified (see [`crate::client::resolve`]).
    pub fn run(
        &self,
        release: &ReleaseManifest,
        bootstrapper: Option<&BootstrapperManifest>,
        relaunch_args: &[String],
        options: &BootstrapOptions,
        progress: Progress<'_>,
        cancel: &Cancel,
    ) -> Result<BootstrapOutcome> {
        let _lock = InstallLock::acquire(self.paths, options.lock_timeout)?;
        let state = StateStore::new(self.paths.clone());
        let extractor = self.extractor.unwrap_or(&Ditto);
        recover_pending(self.paths, &state)?;
        let updater = SelfUpdater { paths: self.paths, content: self.content };
        updater.cleanup_old()?;

        let current = state.read();
        // This platform's binary only: Windows never downloads the mac bundle, nor the reverse.
        if let Some(boot) = bootstrapper
            && let Some(artifact) = boot.platform_artifact()
            && needs_bootstrapper_update(&current.bootstrapper_version, &boot.version)
        {
            log::info!("bootstrapper update {} -> {}; self-updating", current.bootstrapper_version, boot.version);
            updater.prepare(artifact, progress, cancel)?;
            cancel.check()?;
            updater.swap(Some(extractor))?;
            // Recorded before the relaunch, so the new process goes on to the app instead of
            // updating itself again.
            let mut s = state.read();
            s.bootstrapper_version = boot.version.clone();
            state.write(&s)?;
            updater.relaunch(relaunch_args)?;
            return Ok(BootstrapOutcome::SelfUpdateRelaunching);
        }

        let outcome = if IS_MAC {
            MonolithInstaller { paths: self.paths, content: self.content, state: &state, extractor }
                .update(release, &options.update, progress, cancel)?
        } else {
            UpdateEngine { paths: self.paths, content: self.content, state: &state, keys: self.keys }
                .update(release, &options.update, progress, cancel)?
                .outcome
        };

        if let Some(boot) = bootstrapper {
            let mut after = state.read();
            if !after.bootstrapper_version.eq_ignore_ascii_case(&boot.version) {
                after.bootstrapper_version = boot.version.clone();
                state.write(&after)?;
            }
        }

        Ok(match outcome {
            UpdateOutcome::Updated => BootstrapOutcome::Updated,
            UpdateOutcome::UpToDate => BootstrapOutcome::UpToDate,
        })
    }
}

/// Whether [`BootstrapFlow::run`] would change anything: a self-update of this platform's
/// bootstrapper, or a package (on macOS, the whole app) to install, update or remove. Reads the
/// state without the install lock, so it is a hint: what a launcher asks before it shows any UI,
/// and what a running app asks to offer an update.
pub fn needs_work(paths: &InstallPaths, release: &ReleaseManifest, bootstrapper: Option<&BootstrapperManifest>) -> Result<bool> {
    let current = StateStore::new(paths.clone()).read();
    if let Some(boot) = bootstrapper
        && boot.platform_artifact().is_some()
        && needs_bootstrapper_update(&current.bootstrapper_version, &boot.version)
    {
        return Ok(true);
    }
    if IS_MAC {
        let (Some(mac), Some(launch)) = (&release.mac_app, &release.mac_launch) else {
            return Err(Error::other(format!("release {} has no macApp artifact or macLaunch spec", release.release_id)));
        };
        return Ok(!is_installed(&current, mac, &paths.under_root(&launch.bin)?));
    }
    Ok(plan(release, &current, &current.bootstrapper_version, None)?.has_work())
}

/// A different version that is not older, when both parse as SemVer or both are plain dotted
/// numbers (a Windows file version such as `3.1.2.57`); otherwise any difference.
pub fn needs_bootstrapper_update(installed: &str, target: &str) -> bool {
    if installed.eq_ignore_ascii_case(target) {
        return false;
    }
    if let (Some(i), Some(t)) = (PackageVersion::parse(installed), PackageVersion::parse(target)) {
        return t > i;
    }
    match (dotted(installed), dotted(target)) {
        (Some(i), Some(t)) => t > i,
        _ => true,
    }
}

/// `1.2.3.4` as numbers, missing components as 0, so `1.2` equals `1.2.0.0`.
fn dotted(version: &str) -> Option<[u64; 4]> {
    let mut parts = [0; 4];
    for (i, part) in version.trim().split('.').enumerate() {
        if i == 4 || part.is_empty() || !part.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        parts[i] = part.parse().ok()?;
    }
    Some(parts)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_bootstrapper_never_downgrades_but_updates_from_nothing() {
        assert!(needs_bootstrapper_update("", "2.30.1"));
        assert!(needs_bootstrapper_update("2.30.0", "2.30.1"));
        assert!(!needs_bootstrapper_update("2.30.1", "2.30.1"));
        assert!(!needs_bootstrapper_update("2.31.0", "2.30.1"));
        assert!(needs_bootstrapper_update("weird", "2.30.1"));
    }

    #[test]
    fn four_part_file_versions_are_ordered_numerically() {
        assert!(needs_bootstrapper_update("3.1.2.9", "3.1.2.10"));
        assert!(!needs_bootstrapper_update("3.1.2.10", "3.1.2.9"));
        assert!(needs_bootstrapper_update("3.1.2.57", "3.1.3.0"));
        assert!(!needs_bootstrapper_update("3.1.2.0", "3.1.2"));
        assert!(needs_bootstrapper_update("", "3.1.2.57"));
    }
}
