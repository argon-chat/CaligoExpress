//! Against a real deployment, configured from the environment (nothing deployment-specific lives in
//! this repository). Run with `cargo test --test live -- --ignored`:
//!
//! - `CALIGO_LIVE_SEED`: a manifest host; `CALIGO_LIVE_KEY_ID` / `CALIGO_LIVE_KEY`: the pinned key id
//!   and base64 public key; `CALIGO_LIVE_CHANNEL` (default `live`); `CALIGO_LIVE_APP` (optional).
//! - `CALIGO_LIVE_STATE`: a `.caligo/state.json` the .NET client wrote.

use std::collections::BTreeMap;

use caligo::client::{ClientOptions, resolve};
use caligo::content::{ContentSource, http_client};
use caligo::crypto::{decode_pinned, expect_hash, sha256_hex};
use caligo::manifest::{LocalInstallState, PackageManifest};
use caligo::Cancel;

fn env(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|v| !v.is_empty())
}

#[test]
#[ignore = "needs a live deployment from the environment"]
fn a_live_release_resolves_with_verified_signatures_and_package_hashes() {
    let (Some(seed), Some(key_id), Some(key)) = (env("CALIGO_LIVE_SEED"), env("CALIGO_LIVE_KEY_ID"), env("CALIGO_LIVE_KEY")) else {
        panic!("set CALIGO_LIVE_SEED, CALIGO_LIVE_KEY_ID and CALIGO_LIVE_KEY");
    };
    let options = ClientOptions {
        seed_hosts: vec![seed],
        pinned_keys: decode_pinned(&BTreeMap::from([(key_id, key)])).unwrap(),
        scheme: "https".into(),
        region: None,
        app_id: env("CALIGO_LIVE_APP"),
        require_signatures: true,
    };
    let http = http_client().unwrap();
    let cancel = Cancel::new();
    let channel = env("CALIGO_LIVE_CHANNEL").unwrap_or_else(|| "live".into());
    let resolved = resolve(&options, &http, &channel, &cancel).unwrap();
    println!(
        "release {} (seq {}), bootstrapper {:?}",
        resolved.release.release_id,
        resolved.release.release_seq,
        resolved.bootstrapper.as_ref().map(|b| &b.version)
    );
    for pkg in &resolved.release.packages {
        let bytes = resolved.content.get_bytes(&pkg.manifest.key, &cancel).unwrap();
        expect_hash(&pkg.manifest.key, &pkg.manifest.hash, &sha256_hex(&bytes)).unwrap();
        let manifest: PackageManifest = serde_json::from_slice(&bytes).unwrap();
        println!("{} {}: {} files, {} chunks", manifest.name, manifest.version, manifest.files.len(), manifest.chunks.len());
    }
}

#[test]
#[ignore = "needs a state.json from the environment"]
fn a_dotnet_state_file_round_trips() {
    let path = env("CALIGO_LIVE_STATE").expect("set CALIGO_LIVE_STATE");
    let text = std::fs::read_to_string(path).unwrap();
    let state: LocalInstallState = serde_json::from_str(text.trim_start_matches('\u{feff}')).unwrap();
    let original: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).unwrap();
    assert_eq!(serde_json::to_value(&state).unwrap(), original, "nothing is lost or added on a rewrite");
}
