//! The update engine end to end, against a CDN laid out in a temp directory.

use std::fs;
use std::path::Path;
use std::sync::Mutex;

use caligo::content::{ByteProgress, ContentSource, LocalSource};
use caligo::crypto::{KeyMap, load_release, public_key_from_seed, sha256_hex, signed_json};
use caligo::keys::KeyScheme;
use caligo::manifest::{ArtifactRef, ChunkEntry, FileEntry, HookDef, PackageManifest, PackageRef, RegionEntry, ReleaseManifest};
use caligo::paths::InstallPaths;
use caligo::state::StateStore;
use caligo::txn::{StagingArea, TransactionJournal, TxnOp, TxnOpKind, TxnStatus, commit, new_txn_id, recover_pending};
use caligo::update::{UpdateEngine, UpdateOptions, UpdateOutcome};
use caligo::bootstrap::needs_work;
use caligo::{Cancel, Error, IS_MAC, Result, chunk};

const SEED: [u8; 32] = [42; 32];
const CHUNK: usize = 4096;

/// A content source that counts what it serves.
struct Counting {
    inner: LocalSource,
    fetched: Mutex<Vec<String>>,
}

impl ContentSource for Counting {
    fn get_bytes(&self, key: &str, cancel: &Cancel) -> Result<Vec<u8>> {
        self.fetched.lock().unwrap().push(key.to_owned());
        self.inner.get_bytes(key, cancel)
    }
    fn download_to_file(&self, key: &str, d: &Path, h: Option<&str>, p: ByteProgress<'_>, c: &Cancel) -> Result<()> {
        self.fetched.lock().unwrap().push(key.to_owned());
        self.inner.download_to_file(key, d, h, p, c)
    }
}

impl Counting {
    fn chunks_fetched(&self) -> usize {
        self.fetched.lock().unwrap().drain(..).filter(|k| k.starts_with("chunks/")).count()
    }
}

/// Publishes a package version: files over CHUNK bytes get whole chunks of their own, smaller ones
/// share one pack chunk. Returns the release entry.
fn publish(cdn: &Path, version: &str, files: &[(&str, Vec<u8>)], hooks: Vec<HookDef>) -> PackageRef {
    let keys = KeyScheme::for_app(None);
    let mut chunks = Vec::new();
    let mut entries = Vec::new();
    let mut put = |plain: &[u8]| {
        let hash = sha256_hex(plain);
        let path = cdn.join(keys.chunk(&hash));
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, chunk::encode(plain)).unwrap();
        chunks.push(ChunkEntry { hash: hash.clone(), size: plain.len() as u32 });
        hash
    };
    let mut pack = Vec::new();
    let mut packed = Vec::new();
    for (path, content) in files {
        if content.len() > CHUNK {
            let regions = content
                .chunks(CHUNK)
                .map(|piece| RegionEntry { chunk: put(piece), offset: 0, length: piece.len() as u32 })
                .collect();
            entries.push(FileEntry { path: path.to_string(), hash: sha256_hex(content), size: content.len() as u64, regions, exec: false });
        } else {
            packed.push((path, pack.len(), content));
            pack.extend_from_slice(content);
        }
    }
    if !pack.is_empty() {
        let hash = put(&pack);
        for (path, offset, content) in packed {
            entries.push(FileEntry {
                path: path.to_string(),
                hash: sha256_hex(content),
                size: content.len() as u64,
                regions: vec![RegionEntry { chunk: hash.clone(), offset: offset as u32, length: content.len() as u32 }],
                exec: false,
            });
        }
    }
    let manifest = PackageManifest {
        schema_version: 1,
        name: "core".into(),
        version: version.into(),
        target_root: "app".into(),
        chunks,
        files: entries,
        hooks,
    };
    let bytes = serde_json::to_vec(&manifest).unwrap();
    let key = keys.package_manifest("core", version);
    fs::create_dir_all(cdn.join(&key).parent().unwrap()).unwrap();
    fs::write(cdn.join(&key), &bytes).unwrap();
    PackageRef {
        name: "core".into(),
        version: version.into(),
        required: true,
        deps: vec![],
        manifest: ArtifactRef { key, hash: sha256_hex(&bytes), size: bytes.len() as u64 },
    }
}

/// A release, signed and read back through verification as the client would.
fn release(seq: i64, packages: Vec<PackageRef>) -> ReleaseManifest {
    let unsigned: ReleaseManifest = serde_json::from_value(serde_json::json!({
        "schemaVersion": 1, "channel": "live", "releaseSeq": seq, "releaseId": format!("live-{seq}"),
        "createdAt": "2026-01-01T00:00:00+00:00", "packages": packages,
        "launch": { "bin": "app/App.exe", "args": [] },
    }))
    .unwrap();
    let json = signed_json(&unsigned, "test", &SEED).unwrap();
    let keys = KeyMap::from([("test".to_owned(), public_key_from_seed(&SEED).unwrap())]);
    load_release(json.as_bytes(), Some(&keys)).unwrap()
}

fn noise(len: usize, salt: u32) -> Vec<u8> {
    (0..len as u32).map(|i| (i.wrapping_add(salt).wrapping_mul(2_654_435_761) >> 11) as u8).collect()
}

struct Fixture {
    _dir: tempfile::TempDir,
    cdn: std::path::PathBuf,
    paths: InstallPaths,
    source: Counting,
}

fn fixture() -> Fixture {
    let dir = tempfile::tempdir().unwrap();
    let cdn = dir.path().join("cdn");
    fs::create_dir_all(&cdn).unwrap();
    let paths = InstallPaths::new(dir.path().join("root"), "Setup.exe").unwrap();
    let source = Counting { inner: LocalSource::new(&cdn).unwrap(), fetched: Mutex::default() };
    Fixture { _dir: dir, cdn, paths, source }
}

fn run(f: &Fixture, target: &ReleaseManifest) -> Result<UpdateOutcome> {
    let state = StateStore::new(f.paths.clone());
    let keys = KeyScheme::for_app(None);
    let engine = UpdateEngine { paths: &f.paths, content: &f.source, state: &state, keys: &keys };
    Ok(engine.update(target, &UpdateOptions::default(), None, &Cancel::new())?.outcome)
}

#[test]
fn an_update_downloads_only_changed_chunks_removes_dropped_files_and_runs_hooks() {
    let f = fixture();
    let big = noise(CHUNK * 3, 1);
    let v1 = publish(
        &f.cdn,
        "1.0.0",
        &[("App.exe", big.clone()), ("res/a.txt", b"alpha".to_vec()), ("res/old.txt", b"old".to_vec())],
        vec![],
    );
    let r1 = release(1, vec![v1]);
    // The whole-app (macOS) path has its own manifests; this fixture publishes packages only.
    let needs_work = |r: &ReleaseManifest| !IS_MAC && needs_work(&f.paths, r, None).unwrap();
    assert!(IS_MAC || needs_work(&r1), "a fresh root needs everything");
    assert_eq!(run(&f, &r1).unwrap(), UpdateOutcome::Updated);
    assert!(!needs_work(&r1), "installed is up to date");
    let app = f.paths.root().join("app");
    assert_eq!(fs::read(app.join("App.exe")).unwrap(), big);
    assert_eq!(fs::read(app.join("res/old.txt")).unwrap(), b"old");
    assert_eq!(f.source.chunks_fetched(), 4, "three exe chunks and one pack");

    // v2: one exe chunk changes, old.txt goes, new.txt comes, and a hook writes the version.
    let mut big2 = big.clone();
    big2[CHUNK + 7] ^= 0xff;
    let v2 = publish(
        &f.cdn,
        "2.0.0",
        &[("App.exe", big2.clone()), ("res/a.txt", b"alpha".to_vec()), ("res/new.txt", b"new".to_vec())],
        vec![HookDef { id: "v".into(), kind: "emit".into(), query: Some("#version".into()), origin: None, target: Some("version.txt".into()), override_: false }],
    );
    let r2 = release(2, vec![v2.clone()]);
    assert!(IS_MAC || needs_work(&r2), "a new package version is work");
    assert_eq!(run(&f, &r2).unwrap(), UpdateOutcome::Updated);
    assert_eq!(fs::read(app.join("App.exe")).unwrap(), big2);
    assert!(!app.join("res/old.txt").exists());
    assert_eq!(fs::read(app.join("res/new.txt")).unwrap(), b"new");
    assert_eq!(fs::read(app.join("version.txt")).unwrap(), b"2.0.0");
    assert_eq!(f.source.chunks_fetched(), 2, "the changed exe chunk and the new pack; the rest came off disk");

    let state = StateStore::new(f.paths.clone()).read();
    assert_eq!((state.release_seq, state.packages["core"].version.as_str()), (2, "2.0.0"));
    assert!(!f.paths.chunk_cache_dir().exists(), "the cache is dropped after a commit");
    assert_eq!(fs::read_dir(f.paths.journal_dir()).unwrap().count(), 0);

    // Same release again: nothing to do. An older one on the same channel: refused.
    assert_eq!(run(&f, &release(2, vec![v2])).unwrap(), UpdateOutcome::UpToDate);
    let v1_again = publish(&f.cdn, "1.0.0", &[("App.exe", big)], vec![]);
    assert!(matches!(run(&f, &release(1, vec![v1_again])), Err(Error::Other(m)) if m.contains("downgrade")));
}

#[test]
fn a_corrupt_chunk_fails_closed_and_leaves_the_install_untouched() {
    let f = fixture();
    let v1 = publish(&f.cdn, "1.0.0", &[("a.txt", b"one".to_vec())], vec![]);
    run(&f, &release(1, vec![v1])).unwrap();
    let v2 = publish(&f.cdn, "2.0.0", &[("a.txt", b"two".to_vec())], vec![]);
    let pack = KeyScheme::for_app(None).chunk(&sha256_hex(b"two"));
    fs::write(f.cdn.join(pack), chunk::encode(b"evil")).unwrap();
    let err = run(&f, &release(2, vec![v2])).unwrap_err();
    assert!(matches!(err, Error::HashMismatch { .. }), "{err}");
    assert_eq!(fs::read(f.paths.root().join("app/a.txt")).unwrap(), b"one");
    assert_eq!(StateStore::new(f.paths.clone()).read().release_seq, 1);
}

#[test]
fn a_crash_mid_commit_rolls_back_to_the_old_tree_and_a_crash_after_the_state_write_keeps_the_new() {
    for (crash_at, committed) in [("after-op:", false), ("before-state-write", false), ("after-state-write", true)] {
        let f = fixture();
        let state = StateStore::new(f.paths.clone());
        let live_a = f.paths.root().join("app/a.txt");
        let live_b = f.paths.root().join("app/b.txt");
        fs::create_dir_all(live_a.parent().unwrap()).unwrap();
        fs::write(&live_a, "old a").unwrap();
        fs::write(&live_b, "old b").unwrap();
        let mut before = state.read();
        before.release_seq = 1;
        before.release_id = "r1".into();
        state.write(&before).unwrap();

        let id = new_txn_id();
        let staging = StagingArea::new(&f.paths, &id).unwrap();
        fs::write(staging.staged("0"), "new a").unwrap();
        fs::write(staging.staged("2"), "new c").unwrap();
        let ops = vec![
            TxnOp { kind: TxnOpKind::Replace, target_path: live_a.clone(), staged_path: Some(staging.staged("0")), backup_path: Some(staging.backup("0")), old_hash: None, new_hash: None },
            TxnOp { kind: TxnOpKind::Delete, target_path: live_b.clone(), staged_path: None, backup_path: Some(staging.backup("1")), old_hash: None, new_hash: None },
            TxnOp { kind: TxnOpKind::Add, target_path: f.paths.root().join("app/c.txt"), staged_path: Some(staging.staged("2")), backup_path: None, old_hash: None, new_hash: None },
        ];
        let mut after = before.clone();
        after.release_seq = 2;
        after.release_id = "r2".into();
        let mut journal = TransactionJournal { id, status: TxnStatus::Prepared, target_release_seq: 2, target_release_id: "r2".into(), operations: ops, new_state: after };

        let crash = |label: &str| if label.starts_with(crash_at) && label.ends_with(if crash_at == "after-op:" { "c.txt" } else { "" }) { Err(Error::other("crash")) } else { Ok(()) };
        assert!(commit(&f.paths, &state, &mut journal, Some(&crash)).is_err(), "{crash_at}");

        let report = recover_pending(&f.paths, &state).unwrap();
        let c = f.paths.root().join("app/c.txt");
        if committed {
            assert_eq!(report.completed, 1);
            assert_eq!(fs::read_to_string(&live_a).unwrap(), "new a");
            assert!(!live_b.exists());
            assert!(c.exists());
            assert_eq!(state.read().release_seq, 2);
        } else {
            assert_eq!(report.rolled_back, 1, "{crash_at}");
            assert_eq!(fs::read_to_string(&live_a).unwrap(), "old a", "{crash_at}");
            assert_eq!(fs::read_to_string(&live_b).unwrap(), "old b", "{crash_at}");
            assert!(!c.exists(), "{crash_at}");
            assert_eq!(state.read().release_seq, 1);
        }
        assert_eq!(fs::read_dir(f.paths.journal_dir()).unwrap().count(), 0);
    }
}
