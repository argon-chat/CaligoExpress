//! The manifest and state documents, field for field as the .NET client reads and writes them
//! (camelCase names, null fields omitted). Unknown fields are ignored; they still count for the
//! signature, which covers the received text (see [`crate::crypto`]).

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

/// A binary artifact on a mirror. `key` is the mirror-relative path; `hash` (lowercase SHA-256 hex
/// of the bytes) anchors trust, since mirrors are untrusted.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ArtifactRef {
    pub key: String,
    pub hash: String,
    pub size: u64,
}

/// Detached Ed25519 signature over the canonical bytes of a manifest (without this field).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SignatureEnvelope {
    pub key_id: String,
    pub alg: String,
    pub sig: String,
}

/// A trust anchor; `key` is base64 of the raw 32-byte Ed25519 public key.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct PublicKeyEntry {
    pub id: String,
    pub alg: String,
    pub key: String,
}

/// A content mirror. The client fails over between them, region matches first.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct MirrorEntry {
    pub host: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub regions: Option<Vec<String>>,
    #[serde(default)]
    pub priority: i32,
    #[serde(default = "https")]
    pub scheme: String,
}

fn https() -> String {
    "https".into()
}

/// The bootstrap document: mirrors, channel pointers, trust anchors. Fetched from the seed hosts and
/// verified against the key pinned in the binary.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigManifest {
    pub schema_version: i32,
    pub mirrors: Vec<MirrorEntry>,
    pub channels: BTreeMap<String, String>,
    pub public_keys: Vec<PublicKeyEntry>,
    pub min_bootstrapper: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<SignatureEnvelope>,
}

/// One package pinned by a release; `manifest` points at its `package.json`.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PackageRef {
    pub name: String,
    pub version: String,
    pub required: bool,
    #[serde(default)]
    pub deps: Vec<String>,
    pub manifest: ArtifactRef,
}

/// How the bootstrapper starts the app. `bin` is relative to the install root (on macOS the bundle,
/// handed to `open`); `working_dir` defaults to the binary's folder.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchSpec {
    pub bin: String,
    #[serde(default)]
    pub args: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub working_dir: Option<String>,
}

/// The macOS whole-app artifact: one `ditto` zip of a signed bundle, which chunks could not
/// reassemble without breaking its signature. `bundle_name` is what the archive expands to.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonolithRef {
    pub artifact: ArtifactRef,
    pub bundle_name: String,
    pub version: String,
}

/// Pre-decoupling clients need a `bootstrapper` inside release.json; new clients ignore it.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct LegacyBootstrapperRef {
    pub version: String,
    pub full: ArtifactRef,
}

/// The pinned set of package versions for a channel. `release_seq`, issued by the control plane,
/// decides update direction and downgrade protection. Signed.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReleaseManifest {
    pub schema_version: i32,
    pub channel: String,
    pub release_seq: i64,
    pub release_id: String,
    pub created_at: String,
    pub packages: Vec<PackageRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub launch: Option<LaunchSpec>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub bootstrapper: Option<LegacyBootstrapperRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<SignatureEnvelope>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mac_app: Option<MonolithRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mac_launch: Option<LaunchSpec>,
    /// The Linux app: one AppImage, which replaces itself (`appimage.rs`); `bundle_name` is its file
    /// name. There is no Linux bootstrapper and so no launch spec.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub linux_app: Option<MonolithRef>,
    /// The app slug on a multi-app worker, inside the signed payload.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub app: Option<String>,
}

impl ReleaseManifest {
    /// This platform's launch spec.
    pub fn platform_launch(&self) -> Option<&LaunchSpec> {
        if crate::IS_MAC { self.mac_launch.as_ref() } else { self.launch.as_ref() }
    }
}

/// The bootstrapper's own signed pointer, rolled independently of app releases. `full` is the
/// Windows binary, `mac_full` the macOS bundle zip; either may be missing.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootstrapperManifest {
    pub schema_version: i32,
    pub channel: String,
    pub seq: i64,
    pub version: String,
    pub created_at: String,
    pub full: ArtifactRef,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<SignatureEnvelope>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mac_full: Option<ArtifactRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub app: Option<String>,
}

impl BootstrapperManifest {
    /// This platform's binary, if published.
    pub fn platform_artifact(&self) -> Option<&ArtifactRef> {
        if crate::IS_MAC { self.mac_full.as_ref() } else { Some(&self.full) }
    }
}

/// One content-defined chunk; `hash` is the SHA-256 of its plaintext and its CDN address.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChunkEntry {
    pub hash: String,
    pub size: u32,
}

/// Bytes `[offset, offset + length)` of a chunk's plaintext.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct RegionEntry {
    pub chunk: String,
    pub offset: u32,
    pub length: u32,
}

/// One installed file, relative to the package's target root with forward slashes, reassembled by
/// concatenating its regions.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileEntry {
    pub path: String,
    pub hash: String,
    pub size: u64,
    #[serde(default)]
    pub regions: Vec<RegionEntry>,
    #[serde(default)]
    pub exec: bool,
}

/// A post-apply action, evaluated into staged files so it runs inside the transaction.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct HookDef {
    pub id: String,
    pub kind: String,
    #[serde(default)]
    pub query: Option<String>,
    #[serde(default)]
    pub origin: Option<String>,
    #[serde(default)]
    pub target: Option<String>,
    #[serde(default, rename = "over")]
    pub override_: bool,
}

/// The full file listing of one package version; hash-covered by the release's `PackageRef`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PackageManifest {
    pub schema_version: i32,
    pub name: String,
    pub version: String,
    pub target_root: String,
    #[serde(default)]
    pub chunks: Vec<ChunkEntry>,
    #[serde(default)]
    pub files: Vec<FileEntry>,
    #[serde(default)]
    pub hooks: Vec<HookDef>,
}

/// What is installed for one package; `manifest` embeds the applied listing so removals and reuse
/// need nothing from the network.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledPackage {
    pub name: String,
    pub version: String,
    pub manifest_hash: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub manifest: Option<PackageManifest>,
}

/// `.caligo/state.json`: the installed release pointer and per-package versions.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct LocalInstallState {
    pub schema_version: i32,
    pub channel: String,
    pub release_seq: i64,
    pub release_id: String,
    pub bootstrapper_version: String,
    pub packages: BTreeMap<String, InstalledPackage>,
}

impl Default for LocalInstallState {
    fn default() -> Self {
        Self {
            schema_version: 1,
            channel: "live".into(),
            release_seq: 0,
            release_id: String::new(),
            bootstrapper_version: String::new(),
            packages: BTreeMap::new(),
        }
    }
}

/// The slice of `caligo.json` an installer embeds: where to fetch config, which keys to trust.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallerRuntimeConfig {
    pub seed_hosts: Vec<String>,
    pub pinned_keys: BTreeMap<String, String>,
    pub bootstrapper_file_name: String,
    pub default_channel: String,
    #[serde(default = "https")]
    pub scheme: String,
    /// The app slug on a multi-app worker; `None` for the default app's root layout.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub app_id: Option<String>,
}
