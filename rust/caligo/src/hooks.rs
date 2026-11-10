//! Package hooks, evaluated at prepare time into ordinary staged files, so they run inside the
//! transaction and roll back with it. Being pure file generators keeps them idempotent.
//! `emit` writes a string (`#version` is the package version); `copy` duplicates a package file.

use crate::manifest::{HookDef, PackageManifest};
use crate::{Error, Result};

/// A file a hook writes, relative to the package's target root.
pub struct HookOutput {
    pub relative_path: String,
    pub content: Vec<u8>,
}

pub fn run(manifest: &PackageManifest, mut read_file: impl FnMut(&str) -> Option<Vec<u8>>) -> Result<Vec<HookOutput>> {
    manifest
        .hooks
        .iter()
        .map(|hook| {
            let content = match hook.kind.as_str() {
                "emit" => match hook.query.as_deref() {
                    Some("#version") => manifest.version.clone().into_bytes(),
                    other => other.unwrap_or_default().as_bytes().to_vec(),
                },
                "copy" => {
                    let origin = hook
                        .origin
                        .as_deref()
                        .ok_or_else(|| Error::other(format!("copy hook '{}' is missing its origin", hook.id)))?;
                    read_file(origin)
                        .ok_or_else(|| Error::other(format!("copy hook '{}' origin '{origin}' was not found", hook.id)))?
                }
                kind => return Err(Error::other(format!("unknown hook kind '{kind}' (hook '{}')", hook.id))),
            };
            Ok(HookOutput { relative_path: target(hook)?.to_owned(), content })
        })
        .collect()
}

fn target(hook: &HookDef) -> Result<&str> {
    hook.target.as_deref().ok_or_else(|| Error::other(format!("hook '{}' is missing its target", hook.id)))
}
