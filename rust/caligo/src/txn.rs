//! In-place updates as journaled transactions. Prepare materializes new content into the
//! transaction's `staged/` without touching the live tree; commit backs up what it replaces, renames
//! staged files into place, and then writes `state.json`: that single atomic write is the commit
//! point. Recovery, run before any new work, compares each leftover journal's target with the state
//! to decide: committed (clean up) or not (roll back from the backups). The journal is the .NET
//! client's `txn.json`, so either client recovers what the other left.

use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::fs::{delete_dir, delete_file, move_dir, move_file, write_synced};
use crate::manifest::LocalInstallState;
use crate::paths::InstallPaths;
use crate::state::StateStore;
use crate::{Error, Result};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum TxnOpKind {
    /// Overwrite an existing file (the old one is backed up first).
    Replace,
    /// Create a file that did not exist.
    Add,
    /// Remove a file (backed up first).
    Delete,
    /// Swap a whole directory tree: the macOS bundle.
    ReplaceTree,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum TxnStatus {
    Prepared,
    Committing,
    Committed,
}

/// One planned mutation; paths are absolute.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TxnOp {
    pub kind: TxnOpKind,
    pub target_path: PathBuf,
    /// Where new content waits (Replace, Add, ReplaceTree).
    #[serde(default)]
    pub staged_path: Option<PathBuf>,
    /// Where the old content goes so a rollback can restore it (Replace, Delete, ReplaceTree).
    #[serde(default)]
    pub backup_path: Option<PathBuf>,
    #[serde(default)]
    pub old_hash: Option<String>,
    #[serde(default)]
    pub new_hash: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TransactionJournal {
    pub id: String,
    pub status: TxnStatus,
    pub target_release_seq: i64,
    pub target_release_id: String,
    pub operations: Vec<TxnOp>,
    /// What `state.json` becomes at the commit point.
    pub new_state: LocalInstallState,
}

pub fn write_journal(path: &Path, journal: &TransactionJournal) -> Result<()> {
    if let Some(dir) = path.parent() {
        fs::create_dir_all(dir)?;
    }
    let mut tmp = path.as_os_str().to_owned();
    tmp.push(".tmp");
    let tmp = PathBuf::from(tmp);
    write_synced(&tmp, serde_json::to_string_pretty(journal)?.as_bytes())?;
    move_file(&tmp, path)?;
    Ok(())
}

pub fn read_journal(path: &Path) -> Result<TransactionJournal> {
    let text = fs::read_to_string(path)?;
    Ok(serde_json::from_str(text.trim_start_matches('\u{feff}'))?)
}

/// A transaction id: UTC `yyyyMMddHHmmssfff`, a dash, then random hex; 28 characters.
pub fn new_txn_id() -> String {
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    let secs = now.as_secs();
    let (days, rem) = (secs / 86_400, secs % 86_400);
    let (year, month, day) = civil_from_days(days as i64);
    let stamp = format!(
        "{year:04}{month:02}{day:02}{:02}{:02}{:02}{:03}",
        rem / 3600,
        rem % 3600 / 60,
        rem % 60,
        now.subsec_millis()
    );
    let mut id = format!("{stamp}-{}", crate::fs::random_hex(16));
    id.truncate(28);
    id
}

/// Days since 1970-01-01 to a proleptic Gregorian date (Howard Hinnant's algorithm).
fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (yoe + era * 400 + i64::from(month <= 2), month, day)
}

/// A transaction's `staged/` and `backup/`. Tokens are opaque, so live paths never leak into them.
pub struct StagingArea {
    pub staged_dir: PathBuf,
    pub backup_dir: PathBuf,
}

impl StagingArea {
    pub fn new(paths: &InstallPaths, txn_id: &str) -> Result<Self> {
        let staged_dir = paths.txn_staged_dir(txn_id);
        let backup_dir = paths.txn_backup_dir(txn_id);
        fs::create_dir_all(&staged_dir)?;
        fs::create_dir_all(&backup_dir)?;
        Ok(Self { staged_dir, backup_dir })
    }

    pub fn staged(&self, token: &str) -> PathBuf {
        self.staged_dir.join(token)
    }

    pub fn backup(&self, token: &str) -> PathBuf {
        self.backup_dir.join(token)
    }
}

/// Called at labelled points of a commit; an error from it simulates a crash there (tests).
pub type CrashHook<'a> = Option<&'a dyn Fn(&str) -> Result<()>>;

/// Applies a prepared journal, then writes the new state: the commit point.
pub fn commit(paths: &InstallPaths, state: &StateStore, journal: &mut TransactionJournal, crash: CrashHook<'_>) -> Result<()> {
    let hook = |label: &str| crash.map_or(Ok(()), |f| f(label));
    let journal_file = paths.txn_journal_file(&journal.id);

    journal.status = TxnStatus::Committing;
    write_journal(&journal_file, journal)?;
    hook("after-committing-status")?;

    for op in &journal.operations {
        apply(op)?;
        hook(&format!("after-op:{}", op.target_path.display()))?;
    }

    hook("before-state-write")?;
    state.write(&journal.new_state)?;
    hook("after-state-write")?;

    journal.status = TxnStatus::Committed;
    write_journal(&journal_file, journal)?;
    delete_dir_patiently(&paths.txn_dir(&journal.id));
    Ok(())
}

fn apply(op: &TxnOp) -> Result<()> {
    let staged = || op.staged_path.as_deref().ok_or_else(|| Error::other(format!("op for '{}' has no staged content", op.target_path.display())));
    let backup = || -> Result<&Path> {
        let path = op.backup_path.as_deref().ok_or_else(|| Error::other(format!("op for '{}' has no backup path", op.target_path.display())))?;
        if let Some(dir) = path.parent() {
            fs::create_dir_all(dir)?;
        }
        Ok(path)
    };
    match op.kind {
        TxnOpKind::Replace => {
            if op.target_path.is_file() {
                move_file(&op.target_path, backup()?)?;
            }
            move_file(staged()?, &op.target_path)?;
        }
        TxnOpKind::Add => move_file(staged()?, &op.target_path)?,
        TxnOpKind::Delete => {
            if op.target_path.is_file() {
                move_file(&op.target_path, backup()?)?;
            }
        }
        TxnOpKind::ReplaceTree => {
            // Back up the live tree first, so a crash between the two renames is recoverable.
            if op.target_path.is_dir() {
                move_dir(&op.target_path, backup()?, true)?;
            }
            move_dir(staged()?, &op.target_path, true)?;
        }
    }
    Ok(())
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RecoveryReport {
    pub completed: usize,
    pub rolled_back: usize,
    pub discarded: usize,
}

/// Settles every transaction a crash left behind. Filesystem-driven and idempotent: running it again
/// after a second crash is safe.
pub fn recover_pending(paths: &InstallPaths, state: &StateStore) -> Result<RecoveryReport> {
    let mut report = RecoveryReport::default();
    let Ok(entries) = fs::read_dir(paths.journal_dir()) else {
        return Ok(report);
    };
    for entry in entries {
        let dir = entry?.path();
        if !dir.is_dir() {
            continue;
        }
        let journal_file = dir.join("txn.json");
        if !journal_file.is_file() {
            // No intent was recorded, so nothing touched the live tree.
            delete_dir_patiently(&dir);
            report.discarded += 1;
            continue;
        }
        let journal = match read_journal(&journal_file) {
            Ok(j) => j,
            Err(e) => {
                // The journal is written before the first live change, so a torn one means none happened.
                log::warn!("unreadable journal {}; discarding it: {e}", dir.display());
                delete_dir_patiently(&dir);
                report.discarded += 1;
                continue;
            }
        };
        let current = state.read();
        let committed = !journal.target_release_id.is_empty()
            && journal.target_release_seq == current.release_seq
            && journal.target_release_id == current.release_id;
        if committed {
            log::info!("recovery: transaction {} already committed; cleaning up", journal.id);
            report.completed += 1;
        } else {
            log::warn!("recovery: rolling back interrupted transaction {}", journal.id);
            rollback(&journal)?;
            report.rolled_back += 1;
        }
        delete_dir_patiently(&dir);
    }
    Ok(report)
}

fn rollback(journal: &TransactionJournal) -> Result<()> {
    // In reverse, so dependents are undone before their dependencies.
    for op in journal.operations.iter().rev() {
        match op.kind {
            TxnOpKind::Replace | TxnOpKind::Delete => {
                // A backup exists only if the live file was (about to be) changed: restore it.
                if let Some(backup) = op.backup_path.as_deref().filter(|b| b.is_file()) {
                    move_file(backup, &op.target_path)?;
                }
            }
            TxnOpKind::Add => {
                // The staged copy is gone only once it was moved into place.
                if op.staged_path.as_deref().is_some_and(|s| !s.exists()) && op.target_path.is_file() {
                    delete_file(&op.target_path)?;
                }
            }
            TxnOpKind::ReplaceTree => {
                if let Some(backup) = op.backup_path.as_deref().filter(|b| b.is_dir()) {
                    delete_dir(&op.target_path)?;
                    move_dir(backup, &op.target_path, false)?;
                } else if op.staged_path.as_deref().is_some_and(|s| !s.exists()) && op.target_path.is_dir() {
                    // A first install (nothing to back up) that was already applied.
                    delete_dir(&op.target_path)?;
                }
            }
        }
    }
    Ok(())
}

fn delete_dir_patiently(dir: &Path) {
    if let Err(e) = delete_dir(dir) {
        log::warn!("could not remove {}: {e}", dir.display());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn transaction_ids_are_timestamped_and_28_characters() {
        let id = new_txn_id();
        assert_eq!(id.len(), 28);
        assert_eq!(&id[17..18], "-");
        assert!(id[..17].bytes().all(|b| b.is_ascii_digit()));
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        assert_eq!(civil_from_days(20_767), (2026, 11, 10));
    }

    #[test]
    fn a_journal_written_by_the_dotnet_client_reads_back() {
        let json = r#"{
  "id": "20261110120000000-abcdef0123",
  "status": "Committing",
  "targetReleaseSeq": 7,
  "targetReleaseId": "live-1.0.0-7",
  "operations": [
    { "kind": "Replace", "targetPath": "C:\\x\\app\\a.dll", "stagedPath": "C:\\x\\s\\000000", "backupPath": "C:\\x\\b\\000000", "oldHash": null, "newHash": "ab" },
    { "kind": "ReplaceTree", "targetPath": "/x/app/A.app", "stagedPath": null, "backupPath": null, "oldHash": null, "newHash": null }
  ],
  "newState": { "schemaVersion": 1, "channel": "live", "releaseSeq": 7, "releaseId": "live-1.0.0-7", "bootstrapperVersion": "2.0.0", "packages": {} }
}"#;
        let journal: TransactionJournal = serde_json::from_str(json).unwrap();
        assert_eq!(journal.status, TxnStatus::Committing);
        assert_eq!(journal.operations[0].kind, TxnOpKind::Replace);
        assert_eq!(journal.operations[1].kind, TxnOpKind::ReplaceTree);
        assert_eq!(journal.new_state.bootstrapper_version, "2.0.0");
    }
}
