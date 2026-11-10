//! Replaces the running bootstrapper. A running `.exe` cannot be overwritten but can be renamed:
//! the live one moves aside to `.old`, the verified `.new` takes its place, the new one is started and
//! the old process exits; the next start deletes the `.old`. On macOS the bootstrapper is a bundle
//! that arrives as a `ditto` zip; a running app's bundle may be replaced on disk (the kernel keeps the
//! running executable), so it is expanded, swapped the same way and relaunched with `open`.

use std::path::{Path, PathBuf};
use std::process::Command;

use crate::content::ContentSource;
use crate::fs::{delete_dir, delete_file, move_dir, move_file};
use crate::manifest::ArtifactRef;
use crate::monolith::ArchiveExtractor;
use crate::paths::InstallPaths;
use crate::{Cancel, Error, IS_MAC, Progress, Result, UpdateProgress};

pub struct SelfUpdater<'a> {
    pub paths: &'a InstallPaths,
    pub content: &'a dyn ContentSource,
}

impl SelfUpdater<'_> {
    /// Removes the bootstrapper an earlier self-update left behind.
    pub fn cleanup_old(&self) -> Result<()> {
        let old = self.paths.bootstrapper_old();
        if old.is_dir() {
            delete_dir(&old)?;
        } else if old.is_file() {
            delete_file(&old)?;
        } else {
            return Ok(());
        }
        log::info!("removed the previous bootstrapper");
        Ok(())
    }

    /// Downloads and verifies the new bootstrapper into the staging slot, reported as the
    /// `"launcher"` download.
    pub fn prepare(&self, artifact: &ArtifactRef, progress: Progress<'_>, cancel: &Cancel) -> Result<()> {
        self.paths.ensure_control_dirs()?;
        let report = |received: u64, total: Option<u64>| {
            if let Some(f) = progress {
                let total = total.filter(|&t| t > 0).or((artifact.size > 0).then_some(artifact.size));
                f(UpdateProgress::download(Some("launcher"), received, total));
            }
        };
        report(0, None);
        self.content.download_to_file(&artifact.key, &self.paths.bootstrapper_new(), Some(&artifact.hash), Some(&report), cancel)
    }

    /// Live bootstrapper → `.old`, staged one → live.
    pub fn swap(&self, extractor: Option<&dyn ArchiveExtractor>) -> Result<()> {
        let new = self.paths.bootstrapper_new();
        if !new.is_file() {
            return Err(Error::other("no staged bootstrapper to swap in"));
        }
        let (live, old) = (self.paths.bootstrapper_exe(), self.paths.bootstrapper_old());
        if IS_MAC {
            let extractor = extractor.ok_or_else(|| Error::other("the macOS bootstrapper update needs an archive extractor"))?;
            let mut stage = new.as_os_str().to_owned();
            stage.push(".stage");
            let stage = PathBuf::from(stage);
            delete_dir(&stage)?;
            extractor.extract(&new, &stage)?;
            let bundle = staged_bundle(&stage, self.paths.bootstrapper_file_name())?;
            delete_dir(&old)?;
            if live.is_dir() {
                move_dir(live, &old, true)?;
            }
            move_dir(&bundle, live, true)?;
            delete_dir(&stage)?;
            delete_file(&new)?;
            log::info!("swapped in the new bootstrapper bundle");
        } else {
            delete_file(&old)?;
            if live.is_file() {
                move_file(live, &old)?;
            }
            move_file(&new, live)?;
            log::info!("swapped in the new bootstrapper");
        }
        Ok(())
    }

    /// Starts the swapped-in bootstrapper; the current process should exit right after.
    pub fn relaunch(&self, args: &[String]) -> Result<u32> {
        let live = self.paths.bootstrapper_exe();
        let child = if IS_MAC {
            let mut command = Command::new("/usr/bin/open");
            command.arg("-n").arg(live);
            if !args.is_empty() {
                command.arg("--args").args(args);
            }
            command.spawn()?
        } else {
            let mut command = Command::new(live);
            command.args(args);
            if let Some(dir) = live.parent() {
                command.current_dir(dir);
            }
            command.spawn()?
        };
        Ok(child.id())
    }
}

/// The `.app` inside an expanded bootstrapper archive: the expected name, or else the only bundle
/// there (the build may name it after its title; the destination is the live, correctly named one).
pub fn staged_bundle(stage: &Path, expected: &str) -> Result<PathBuf> {
    let named = stage.join(expected);
    if named.is_dir() {
        return Ok(named);
    }
    let apps: Vec<PathBuf> = std::fs::read_dir(stage)?
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.is_dir() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("app")))
        .collect();
    match apps.as_slice() {
        [one] => Ok(one.clone()),
        [] => Err(Error::other(format!("the staged bootstrapper archive has no .app bundle (expected '{expected}')"))),
        many => Err(Error::other(format!("the staged bootstrapper archive has {} .app bundles; expected one", many.len()))),
    }
}
