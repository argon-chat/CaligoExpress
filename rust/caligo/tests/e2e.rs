//! Installs from a running control plane that the .NET publisher filled, the full chain: signed config
//! from the seed host, signed release and bootstrapper pointer, package manifest and chunks from the S3
//! mirror. `src/CaligoExpress.Cloud/dev/e2e.sh` sets it up and runs it; by hand:
//!
//! - `CALIGO_E2E_SEED` (`localhost:8787`), `CALIGO_E2E_SCHEME` (`http`), `CALIGO_E2E_KEY` (base64 k1 public key);
//! - `CALIGO_E2E_ROOT`: the install root, kept between runs so the next one is an update;
//! - `CALIGO_E2E_SRC`: the published app directory, which the installed `app/` must equal;
//! - `CALIGO_E2E_VERSION`: the package version the release must carry;
//! - `CALIGO_E2E_MAX_CHUNKS` (optional): the most chunks this run may download.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use caligo::client::{ClientOptions, resolve};
use caligo::content::{ByteProgress, ContentSource, http_client};
use caligo::crypto::{decode_pinned, sha256_file};
use caligo::paths::InstallPaths;
use caligo::self_update::SelfUpdater;
use caligo::state::StateStore;
use caligo::update::{UpdateEngine, UpdateOptions};
use caligo::{Cancel, Result};

fn env(name: &str) -> String {
    std::env::var(name).unwrap_or_else(|_| panic!("set {name}"))
}

struct Counting<'a> {
    inner: &'a dyn ContentSource,
    chunks: Mutex<usize>,
}

impl ContentSource for Counting<'_> {
    fn get_bytes(&self, key: &str, cancel: &Cancel) -> Result<Vec<u8>> {
        if key.starts_with("chunks/") {
            *self.chunks.lock().unwrap() += 1;
        }
        self.inner.get_bytes(key, cancel)
    }
    fn download_to_file(&self, key: &str, d: &Path, h: Option<&str>, p: ByteProgress<'_>, c: &Cancel) -> Result<()> {
        self.inner.download_to_file(key, d, h, p, c)
    }
}

fn files(root: &Path) -> BTreeMap<String, String> {
    fn walk(root: &Path, dir: &Path, out: &mut BTreeMap<String, String>) {
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                walk(root, &path, out);
            } else {
                let rel = path.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/");
                out.insert(rel, sha256_file(&path).unwrap());
            }
        }
    }
    let mut out = BTreeMap::new();
    walk(root, root, &mut out);
    out
}

#[test]
#[ignore = "needs a running control plane (dev/e2e.sh)"]
fn installs_and_updates_from_a_running_control_plane() {
    let options = ClientOptions {
        seed_hosts: vec![env("CALIGO_E2E_SEED")],
        pinned_keys: decode_pinned(&BTreeMap::from([("k1".to_owned(), env("CALIGO_E2E_KEY"))])).unwrap(),
        scheme: std::env::var("CALIGO_E2E_SCHEME").unwrap_or_else(|_| "http".into()),
        region: None,
        app_id: None,
        require_signatures: true,
    };
    let cancel = Cancel::new();
    let resolved = resolve(&options, &http_client().unwrap(), "live", &cancel).unwrap();
    let version = env("CALIGO_E2E_VERSION");
    assert_eq!(resolved.release.packages[0].version, version);

    let root = PathBuf::from(env("CALIGO_E2E_ROOT"));
    let paths = InstallPaths::new(&root, "Setup.exe").unwrap();
    let state = StateStore::new(paths.clone());
    let counting = Counting { inner: &resolved.content, chunks: Mutex::new(0) };
    let progress = |p: caligo::UpdateProgress| {
        if let Some(f) = p.fraction {
            assert!((0.0..=1.0).contains(&f), "{p:?}");
        }
    };
    let result = UpdateEngine { paths: &paths, content: &counting, state: &state, keys: &resolved.keys }
        .update(&resolved.release, &UpdateOptions::default(), Some(&progress), &cancel)
        .unwrap();
    let downloaded = *counting.chunks.lock().unwrap();
    println!("{:?}: {} file operations, {downloaded} chunks downloaded", result.outcome, result.file_operations);
    if let Ok(max) = std::env::var("CALIGO_E2E_MAX_CHUNKS")
        && !max.is_empty()
    {
        assert!(downloaded <= max.parse().unwrap(), "downloaded {downloaded} chunks");
    }

    assert_eq!(files(&root.join("app")), files(Path::new(&env("CALIGO_E2E_SRC"))), "app/ equals the published tree");
    let installed = state.read();
    assert_eq!(installed.release_id, resolved.release.release_id);
    assert_eq!(installed.packages["core"].version, version);

    // The bootstrapper pointer: download, verify, swap in (no relaunch here).
    let boot = resolved.bootstrapper.as_ref().expect("the channel has a bootstrapper");
    let updater = SelfUpdater { paths: &paths, content: &resolved.content };
    updater.prepare(&boot.full, None, &cancel).unwrap();
    updater.swap(None).unwrap();
    assert_eq!(sha256_file(paths.bootstrapper_exe()).unwrap(), boot.full.hash);
}
