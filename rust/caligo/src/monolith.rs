//! The macOS whole-app update. A notarized `.app` cannot be reassembled from chunks without breaking
//! its signature (symlinks, exec bits, the stapled ticket), so the bundle comes as one
//! content-addressed `ditto` zip, is expanded with `ditto` and swapped in by one journaled
//! `ReplaceTree` operation, which the same commit and recovery cover.

use std::path::Path;
use std::process::Command;

use crate::content::ContentSource;
use crate::manifest::{InstalledPackage, LocalInstallState, MonolithRef, ReleaseManifest};
use crate::paths::InstallPaths;
use crate::state::StateStore;
use crate::txn::{StagingArea, TransactionJournal, TxnOp, TxnOpKind, TxnStatus, commit, new_txn_id, recover_pending};
use crate::update::{UpdateOptions, UpdateOutcome, check_downgrade};
use crate::{Cancel, Error, Phase, Progress, Result, UpdateProgress};

/// The state slot recording the installed whole-app version.
pub const MAC_APP_PACKAGE: &str = "@macapp";

/// Expands a whole-app archive into a directory.
pub trait ArchiveExtractor: Send + Sync {
    fn extract(&self, archive: &Path, destination: &Path) -> Result<()>;
}

/// `ditto -x -k`, which keeps symlinks, permissions and extended attributes: a generic zip reader
/// would drop them and invalidate the code signature.
pub struct Ditto;

impl ArchiveExtractor for Ditto {
    fn extract(&self, archive: &Path, destination: &Path) -> Result<()> {
        std::fs::create_dir_all(destination)?;
        let output = Command::new("/usr/bin/ditto").arg("-x").arg("-k").arg(archive).arg(destination).output()?;
        if !output.status.success() {
            return Err(Error::other(format!(
                "ditto failed ({}) extracting '{}': {}",
                output.status,
                archive.display(),
                String::from_utf8_lossy(&output.stderr).trim()
            )));
        }
        Ok(())
    }
}

pub struct MonolithInstaller<'a> {
    pub paths: &'a InstallPaths,
    pub content: &'a dyn ContentSource,
    pub state: &'a StateStore,
    pub extractor: &'a dyn ArchiveExtractor,
}

impl MonolithInstaller<'_> {
    pub fn update(&self, target: &ReleaseManifest, options: &UpdateOptions, progress: Progress<'_>, cancel: &Cancel) -> Result<UpdateOutcome> {
        let report = |p: UpdateProgress| {
            if let Some(f) = progress {
                f(p)
            }
        };
        recover_pending(self.paths, self.state)?;
        let current = self.state.read();
        report(UpdateProgress::phase(Phase::Plan));

        let mac = target.mac_app.as_ref().ok_or_else(|| Error::other(format!("release {} has no macApp artifact", target.release_id)))?;
        let launch = target.mac_launch.as_ref().ok_or_else(|| Error::other(format!("release {} has no macLaunch spec", target.release_id)))?;
        check_downgrade(target, &current, options.allow_rollback)?;

        let app_dir = self.paths.under_root(&launch.bin)?;
        if is_installed(&current, mac, &app_dir) {
            log::info!("mac app already at {}; nothing to do", mac.version);
            return Ok(UpdateOutcome::UpToDate);
        }

        self.paths.ensure_control_dirs()?;
        let txn_id = new_txn_id();
        let staging = StagingArea::new(self.paths, &txn_id)?;

        let zip = staging.staged_dir.join("download.zip");
        log::info!("downloading mac app {} ({} bytes)", mac.version, mac.artifact.size);
        report(UpdateProgress::download(Some("app"), 0, Some(mac.artifact.size)));
        let on_bytes = |received: u64, total: Option<u64>| {
            report(UpdateProgress::download(Some("app"), received, total.filter(|&t| t > 0).or(Some(mac.artifact.size))));
        };
        self.content.download_to_file(&mac.artifact.key, &zip, Some(&mac.artifact.hash), Some(&on_bytes), cancel)?;

        report(UpdateProgress { item: Some("app"), ..UpdateProgress::phase(Phase::Install) });
        let extracted = staging.staged_dir.join("extract");
        self.extractor.extract(&zip, &extracted)?;
        let bundle = extracted.join(&mac.bundle_name);
        if !bundle.is_dir() {
            return Err(Error::other(format!(
                "archive for release {} did not contain expected bundle '{}'",
                target.release_id, mac.bundle_name
            )));
        }

        let mut journal = TransactionJournal {
            id: txn_id,
            status: TxnStatus::Prepared,
            target_release_seq: target.release_seq,
            target_release_id: target.release_id.clone(),
            operations: vec![TxnOp {
                kind: TxnOpKind::ReplaceTree,
                target_path: app_dir,
                staged_path: Some(bundle),
                backup_path: Some(staging.backup("app")),
                old_hash: None,
                new_hash: Some(mac.artifact.hash.clone()),
            }],
            new_state: LocalInstallState {
                schema_version: 1,
                channel: target.channel.clone(),
                release_seq: target.release_seq,
                release_id: target.release_id.clone(),
                bootstrapper_version: current.bootstrapper_version.clone(),
                packages: [(
                    MAC_APP_PACKAGE.to_owned(),
                    InstalledPackage {
                        name: MAC_APP_PACKAGE.into(),
                        version: mac.version.clone(),
                        manifest_hash: mac.artifact.hash.clone(),
                        manifest: None,
                    },
                )]
                .into_iter()
                .collect(),
            },
        };
        cancel.check()?;
        log::info!("committing whole-app swap for release {}", target.release_id);
        commit(self.paths, self.state, &mut journal, None)?;
        report(UpdateProgress::phase(Phase::Done));
        Ok(UpdateOutcome::Updated)
    }
}

/// The recorded version and artifact hash match and the bundle is on disk.
fn is_installed(current: &LocalInstallState, mac: &MonolithRef, app_dir: &Path) -> bool {
    current.packages.get(MAC_APP_PACKAGE).is_some_and(|p| {
        p.version.eq_ignore_ascii_case(&mac.version) && p.manifest_hash.eq_ignore_ascii_case(&mac.artifact.hash)
    }) && app_dir.is_dir()
}
