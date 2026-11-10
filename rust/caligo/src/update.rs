//! The chunked package update (Windows; macOS installs the whole bundle, see [`crate::monolith`]).
//!
//! After recovery and planning, every changed file is reassembled into the staging area from chunks
//! already on disk (found by hash in the installed package manifests) plus the missing ones, which
//! are downloaded first, in parallel, into the chunk cache: only the difference crosses the wire.
//! Each file is hash-verified, and the lot is committed as one journaled transaction. The release
//! must already be verified; package manifest, chunk and file hashes chain that trust to every byte.

use std::collections::{HashMap, HashSet};
use std::fs::{self, File};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::time::Duration;

use crate::content::ContentSource;
use crate::crypto::{Sha256, expect_hash, sha256_hex};
use crate::keys::KeyScheme;
use crate::manifest::{FileEntry, InstalledPackage, LocalInstallState, PackageManifest, PackageRef, ReleaseManifest};
use crate::paths::InstallPaths;
use crate::planner::{PackageAction, UpdatePlan, plan};
use crate::state::StateStore;
use crate::txn::{StagingArea, TransactionJournal, TxnOp, TxnOpKind, TxnStatus, commit, new_txn_id, recover_pending};
use crate::{Cancel, Error, Phase, Progress, Result, UpdateProgress, hooks};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UpdateOutcome {
    UpToDate,
    Updated,
}

#[derive(Clone, Debug)]
pub struct UpdateOptions {
    /// Apply a release older than the installed one on the same channel.
    pub allow_rollback: bool,
    /// Missing chunks downloaded at once: half the cores by default.
    pub download_parallelism: usize,
}

impl Default for UpdateOptions {
    fn default() -> Self {
        let cores = std::thread::available_parallelism().map_or(2, |n| n.get());
        Self { allow_rollback: false, download_parallelism: (cores / 2).max(1) }
    }
}

#[derive(Debug)]
pub struct UpdateResult {
    pub outcome: UpdateOutcome,
    pub plan: UpdatePlan,
    pub file_operations: usize,
}

/// Release sequences only compare within a channel: switching channels is deliberate, not a downgrade.
pub(crate) fn check_downgrade(target: &ReleaseManifest, current: &LocalInstallState, allow_rollback: bool) -> Result<()> {
    if target.channel.eq_ignore_ascii_case(&current.channel) && target.release_seq < current.release_seq && !allow_rollback {
        return Err(Error::other(format!(
            "downgrade blocked: target release {} is older than installed {} on channel {}",
            target.release_seq, current.release_seq, current.channel
        )));
    }
    Ok(())
}

pub struct UpdateEngine<'a> {
    pub paths: &'a InstallPaths,
    pub content: &'a dyn ContentSource,
    pub state: &'a StateStore,
    pub keys: &'a KeyScheme,
}

struct Syncing {
    pkg: PackageRef,
    manifest: PackageManifest,
    manifest_hash: String,
}

impl UpdateEngine<'_> {
    pub fn update(&self, target: &ReleaseManifest, options: &UpdateOptions, progress: Progress<'_>, cancel: &Cancel) -> Result<UpdateResult> {
        let report = |p: UpdateProgress| {
            if let Some(f) = progress {
                f(p)
            }
        };
        recover_pending(self.paths, self.state)?;
        let current = self.state.read();
        report(UpdateProgress::phase(Phase::Plan));
        check_downgrade(target, &current, options.allow_rollback)?;

        let plan = plan(target, &current, &current.bootstrapper_version, None)?;
        if plan.actionable().next().is_none() {
            log::info!("already up to date at release {}", current.release_id);
            return Ok(UpdateResult { outcome: UpdateOutcome::UpToDate, plan, file_operations: 0 });
        }

        self.paths.ensure_control_dirs()?;
        let local = self.local_chunk_index(&current);

        let mut syncing = Vec::new();
        for pp in plan.packages.iter().filter(|p| p.action == PackageAction::Sync) {
            cancel.check()?;
            let pkg = pp.target.clone().expect("a sync plan carries its target");
            let (manifest, manifest_hash) = self.fetch_package_manifest(&pkg, cancel)?;
            syncing.push(Syncing { pkg, manifest, manifest_hash });
        }

        // Exactly what must come over the wire: chunks of changed files that are neither on disk nor
        // cached, deduped by hash.
        let mut missing: Vec<(String, u32)> = Vec::new();
        let mut counted = HashSet::new();
        let (mut changed, mut unchanged, mut reused) = (0usize, 0usize, 0usize);
        for s in &syncing {
            let old = old_hash_by_path(&current, &s.pkg.name);
            let sizes: HashMap<&str, u32> = s.manifest.chunks.iter().map(|c| (c.hash.as_str(), c.size)).collect();
            for file in &s.manifest.files {
                if self.is_unchanged(&s.manifest.target_root, file, &old)? {
                    unchanged += 1;
                    continue;
                }
                changed += 1;
                for region in &file.regions {
                    if local.contains_key(&region.chunk) || self.paths.chunk_cache_file(&region.chunk).is_file() {
                        reused += 1;
                    } else if counted.insert(region.chunk.clone()) {
                        let size = *sizes.get(region.chunk.as_str()).ok_or_else(|| {
                            Error::other(format!("{} lists chunk {} that its chunk table does not", s.pkg.name, region.chunk))
                        })?;
                        missing.push((region.chunk.clone(), size));
                    }
                }
            }
        }
        let total: u64 = missing.iter().map(|(_, size)| u64::from(*size)).sum();
        log::info!(
            "sync {}: {changed} changed / {unchanged} unchanged files; chunks {reused} reused, {} to download ({total} bytes)",
            target.release_id,
            missing.len()
        );

        if !missing.is_empty() {
            report(UpdateProgress::download(None, 0, Some(total)));
            self.prefetch(&missing, total, options.download_parallelism, progress, cancel)?;
        }

        let txn_id = new_txn_id();
        let staging = StagingArea::new(self.paths, &txn_id)?;
        let mut builder = OpBuilder { paths: self.paths, staging: &staging, ops: Vec::new(), next: 0 };
        let mut assembler = Assembler { engine: self, local: &local, last: None };
        let mut packages = current.packages.clone();

        report(UpdateProgress::phase(Phase::Install));
        for pp in &plan.packages {
            cancel.check()?;
            match pp.action {
                PackageAction::Skip => {}
                PackageAction::Remove => {
                    if let Some(manifest) = current.packages.get(&pp.name).and_then(|p| p.manifest.as_ref()) {
                        builder.deletions(manifest, &HashSet::new())?;
                    }
                    packages.remove(&pp.name);
                }
                PackageAction::Sync => {
                    let s = syncing.iter().find(|s| s.pkg.name == pp.name).expect("every sync was fetched");
                    let installed = self.sync_package(s, &current, &mut assembler, &mut builder, cancel)?;
                    packages.insert(pp.name.clone(), installed);
                }
            }
        }

        let ops = builder.ops;
        if ops.is_empty() {
            crate::fs::delete_dir(&self.paths.txn_dir(&txn_id))?;
            return Ok(UpdateResult { outcome: UpdateOutcome::UpToDate, plan, file_operations: 0 });
        }
        let file_operations = ops.len();
        let mut journal = TransactionJournal {
            id: txn_id,
            status: TxnStatus::Prepared,
            target_release_seq: target.release_seq,
            target_release_id: target.release_id.clone(),
            operations: ops,
            new_state: LocalInstallState {
                schema_version: 1,
                channel: target.channel.clone(),
                release_seq: target.release_seq,
                release_id: target.release_id.clone(),
                // The bootstrapper moves through the self-updater.
                bootstrapper_version: current.bootstrapper_version.clone(),
                packages,
            },
        };
        log::info!("committing {file_operations} file operations for release {}", target.release_id);
        cancel.check()?;
        commit(self.paths, self.state, &mut journal, None)?;

        // Every chunk now lives in the app tree; the cache only served to resume an interrupted run.
        if let Err(e) = crate::fs::delete_dir(&self.paths.chunk_cache_dir()) {
            log::warn!("could not clean the chunk cache: {e}");
        }
        report(UpdateProgress::phase(Phase::Done));
        Ok(UpdateResult { outcome: UpdateOutcome::Updated, plan, file_operations })
    }

    fn sync_package(
        &self,
        s: &Syncing,
        current: &LocalInstallState,
        assembler: &mut Assembler<'_, '_>,
        builder: &mut OpBuilder<'_>,
        cancel: &Cancel,
    ) -> Result<InstalledPackage> {
        let old = old_hash_by_path(current, &s.pkg.name);
        let mut new_paths = HashSet::new();
        for file in &s.manifest.files {
            cancel.check()?;
            new_paths.insert(file.path.clone());
            if self.is_unchanged(&s.manifest.target_root, file, &old)? {
                continue;
            }
            let token = builder.token();
            assembler.reassemble(file, &builder.staging.staged(&token), cancel)?;
            builder.write(&s.manifest.target_root, &file.path, token, &file.hash)?;
        }
        if let Some(old_manifest) = current.packages.get(&s.pkg.name).and_then(|p| p.manifest.as_ref()) {
            builder.deletions(old_manifest, &new_paths)?;
        }

        // A copy hook reads the file this transaction writes, if it writes one, else the live one.
        let outputs = hooks::run(&s.manifest, |relative| {
            let live = self.paths.installed_file(&s.manifest.target_root, relative).ok()?;
            let staged = builder.ops.iter().rev().find(|op| op.target_path == live).and_then(|op| op.staged_path.clone());
            fs::read(staged.unwrap_or(live)).ok()
        })?;
        for output in outputs {
            let token = builder.token();
            crate::fs::write_synced(&builder.staging.staged(&token), &output.content)?;
            builder.write(&s.manifest.target_root, &output.relative_path, token, &sha256_hex(&output.content))?;
        }

        Ok(InstalledPackage {
            name: s.pkg.name.clone(),
            version: s.pkg.version.clone(),
            manifest_hash: s.manifest_hash.clone(),
            manifest: Some(s.manifest.clone()),
        })
    }

    fn is_unchanged(&self, target_root: &str, file: &FileEntry, old: &HashMap<String, String>) -> Result<bool> {
        Ok(old.get(&file.path).is_some_and(|h| h.eq_ignore_ascii_case(&file.hash))
            && self.paths.installed_file(target_root, &file.path)?.is_file())
    }

    fn fetch_package_manifest(&self, pkg: &PackageRef, cancel: &Cancel) -> Result<(PackageManifest, String)> {
        let bytes = self.content.get_bytes(&pkg.manifest.key, cancel)?;
        let actual = sha256_hex(&bytes);
        expect_hash(&pkg.manifest.key, &pkg.manifest.hash, &actual)?;
        let text = std::str::from_utf8(&bytes).map_err(|_| Error::other(format!("{} is not UTF-8", pkg.manifest.key)))?;
        Ok((serde_json::from_str(text.trim_start_matches('\u{feff}'))?, actual))
    }

    /// Every chunk hash on disk → one place to read it: a whole-chunk region of an installed file.
    /// (A pack chunk's slice is only part of one file, so it cannot be recovered from one.)
    fn local_chunk_index(&self, current: &LocalInstallState) -> HashMap<String, ChunkSource> {
        let mut index = HashMap::new();
        for manifest in current.packages.values().filter_map(|p| p.manifest.as_ref()) {
            let sizes: HashMap<&str, u32> = manifest.chunks.iter().map(|c| (c.hash.as_str(), c.size)).collect();
            for file in &manifest.files {
                let Ok(path) = self.paths.installed_file(&manifest.target_root, &file.path) else {
                    continue;
                };
                let mut offset = 0u64;
                for r in &file.regions {
                    if r.offset == 0 && sizes.get(r.chunk.as_str()) == Some(&r.length) {
                        index.entry(r.chunk.clone()).or_insert_with(|| ChunkSource { path: path.clone(), offset, length: r.length });
                    }
                    offset += u64::from(r.length);
                }
            }
        }
        index
    }

    /// Downloads the missing chunks into the cache, `parallelism` at a time, verified, reporting
    /// cumulative plaintext bytes (so the fraction matches the manifest's sizes).
    fn prefetch(&self, missing: &[(String, u32)], total: u64, parallelism: usize, progress: Progress<'_>, cancel: &Cancel) -> Result<()> {
        let next = AtomicUsize::new(0);
        let received = AtomicU64::new(0);
        let failure: Mutex<Option<Error>> = Mutex::new(None);
        let stop = Cancel::new();
        std::thread::scope(|scope| {
            for _ in 0..parallelism.clamp(1, missing.len()) {
                scope.spawn(|| {
                    loop {
                        if cancel.is_cancelled() || stop.is_cancelled() {
                            return;
                        }
                        let i = next.fetch_add(1, Ordering::Relaxed);
                        let Some((hash, size)) = missing.get(i) else {
                            return;
                        };
                        if let Err(e) = self.cache_chunk(hash, cancel) {
                            stop.cancel();
                            failure.lock().unwrap().get_or_insert(e);
                            return;
                        }
                        let r = received.fetch_add(u64::from(*size), Ordering::Relaxed) + u64::from(*size);
                        if let Some(f) = progress {
                            f(UpdateProgress::download(None, r, Some(total)));
                        }
                    }
                });
            }
        });
        cancel.check()?;
        match failure.into_inner().unwrap() {
            Some(e) => Err(e),
            None => Ok(()),
        }
    }

    fn cache_chunk(&self, hash: &str, cancel: &Cancel) -> Result<Vec<u8>> {
        let file = self.paths.chunk_cache_file(hash);
        if let Ok(cached) = fs::read(&file)
            && sha256_hex(&cached) == hash
        {
            return Ok(cached);
        }
        let plain = self.download_chunk(hash, cancel)?;
        if let Some(dir) = file.parent() {
            fs::create_dir_all(dir)?;
        }
        // Through a temp name, so a crash never leaves a torn chunk under its hash.
        crate::fs::write_atomic(&file, &plain, false)?;
        Ok(plain)
    }

    /// A chunk's verified plaintext. Transient failures (a network blip, a corrupt object) are retried
    /// with backoff; each attempt goes through the mirror failover again. A genuine 404 is not retried.
    fn download_chunk(&self, hash: &str, cancel: &Cancel) -> Result<Vec<u8>> {
        const ATTEMPTS: u32 = 4;
        let key = self.keys.chunk(hash);
        let mut attempt = 1;
        loop {
            let result = self.content.get_bytes(&key, cancel).and_then(|stored| {
                let plain = crate::chunk::decode(&stored)?;
                expect_hash(&key, hash, &sha256_hex(&plain))?;
                Ok(plain)
            });
            match result {
                Err(e) if attempt < ATTEMPTS && e.is_transient() && !cancel.is_cancelled() => {
                    let delay = 400u64 << (attempt - 1);
                    log::warn!("chunk {key} failed (attempt {attempt}/{ATTEMPTS}): {e}; retrying in {delay} ms");
                    sleep_cancellable(Duration::from_millis(delay), cancel)?;
                    attempt += 1;
                }
                other => return other,
            }
        }
    }
}

fn sleep_cancellable(total: Duration, cancel: &Cancel) -> Result<()> {
    let step = Duration::from_millis(50);
    let mut left = total;
    while !left.is_zero() {
        cancel.check()?;
        let d = left.min(step);
        std::thread::sleep(d);
        left -= d;
    }
    cancel.check()
}

fn old_hash_by_path(current: &LocalInstallState, package: &str) -> HashMap<String, String> {
    current
        .packages
        .get(package)
        .and_then(|p| p.manifest.as_ref())
        .map(|m| m.files.iter().map(|f| (f.path.clone(), f.hash.clone())).collect())
        .unwrap_or_default()
}

struct ChunkSource {
    path: PathBuf,
    offset: u64,
    length: u32,
}

struct OpBuilder<'a> {
    paths: &'a InstallPaths,
    staging: &'a StagingArea,
    ops: Vec<TxnOp>,
    next: u32,
}

impl OpBuilder<'_> {
    fn token(&mut self) -> String {
        let t = format!("{:06}", self.next);
        self.next += 1;
        t
    }

    /// Replace when the target exists, else Add; the content is already staged under `token`.
    fn write(&mut self, target_root: &str, relative: &str, token: String, new_hash: &str) -> Result<()> {
        let target_path = self.paths.installed_file(target_root, relative)?;
        let exists = target_path.is_file();
        self.ops.push(TxnOp {
            kind: if exists { TxnOpKind::Replace } else { TxnOpKind::Add },
            target_path,
            staged_path: Some(self.staging.staged(&token)),
            backup_path: exists.then(|| self.staging.backup(&token)),
            old_hash: None,
            new_hash: Some(new_hash.to_owned()),
        });
        Ok(())
    }

    /// Deletes every file of `old` that is not in `keep` and is still on disk.
    fn deletions(&mut self, old: &PackageManifest, keep: &HashSet<String>) -> Result<()> {
        for file in old.files.iter().filter(|f| !keep.contains(&f.path)) {
            let target_path = self.paths.installed_file(&old.target_root, &file.path)?;
            if target_path.is_file() {
                let token = self.token();
                self.ops.push(TxnOp {
                    kind: TxnOpKind::Delete,
                    target_path,
                    staged_path: None,
                    backup_path: Some(self.staging.backup(&token)),
                    old_hash: Some(file.hash.clone()),
                    new_hash: None,
                });
            }
        }
        Ok(())
    }
}

/// Reassembles files region by region: a verified local copy first (a drifted file falls back),
/// then the cache, then a last-resort download. Every source is hash-checked.
struct Assembler<'e, 'a> {
    engine: &'e UpdateEngine<'a>,
    local: &'e HashMap<String, ChunkSource>,
    /// The last chunk read: a pack chunk holds many small files in a row.
    last: Option<(String, Vec<u8>)>,
}

impl Assembler<'_, '_> {
    fn reassemble(&mut self, file: &FileEntry, staged: &Path, cancel: &Cancel) -> Result<()> {
        let mut out = File::create(staged)?;
        let mut hasher = Sha256::new();
        for region in &file.regions {
            cancel.check()?;
            let chunk = self.chunk(&region.chunk, cancel)?;
            let (start, end) = (region.offset as usize, region.offset as usize + region.length as usize);
            let slice = chunk.get(start..end).ok_or_else(|| {
                Error::other(format!("{}: region {start}..{end} is outside chunk {}", file.path, region.chunk))
            })?;
            out.write_all(slice)?;
            hasher.update(slice);
        }
        out.sync_all()?;
        drop(out);
        expect_hash(&file.path, &file.hash, &hasher.finish_hex())?;
        #[cfg(unix)]
        if file.exec {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(staged, fs::Permissions::from_mode(0o755))?;
        }
        Ok(())
    }

    fn chunk(&mut self, hash: &str, cancel: &Cancel) -> Result<&[u8]> {
        if self.last.as_ref().is_none_or(|(h, _)| h != hash) {
            let bytes = match self.local.get(hash).and_then(|src| read_verified(src, hash)) {
                Some(bytes) => bytes,
                None => self.engine.cache_chunk(hash, cancel)?,
            };
            self.last = Some((hash.to_owned(), bytes));
        }
        Ok(&self.last.as_ref().expect("just filled").1)
    }
}

fn read_verified(src: &ChunkSource, hash: &str) -> Option<Vec<u8>> {
    let mut file = File::open(&src.path).ok()?;
    file.seek(SeekFrom::Start(src.offset)).ok()?;
    let mut buf = vec![0u8; src.length as usize];
    file.read_exact(&mut buf).ok()?;
    (sha256_hex(&buf) == hash).then_some(buf)
}
