//! The Linux whole-app update. On Linux the app is one AppImage that updates itself: the image the
//! signed release names (`linuxApp`) is downloaded beside the running one, checked against its
//! SHA-256, given the running image's permissions and renamed over it. The running process keeps
//! its mount of the old image (an open file outlives its name), so the new version is the one the
//! next launch starts.
//!
//! Nothing is recorded: the running build's own version is the installed version.

use std::ffi::OsString;
use std::fs;
use std::os::unix::fs::PermissionsExt;
use std::path::Path;

use crate::bootstrap::needs_bootstrapper_update;
use crate::content::{ByteProgress, ContentSource};
use crate::manifest::{MonolithRef, ReleaseManifest};
use crate::{Cancel, Error, Result};

/// The release's AppImage when it is newer than `running` (SemVer, or a dotted file version such
/// as `3.1.3.82`). Never an older one: a rollback is a newer release.
pub fn update_for<'a>(release: &'a ReleaseManifest, running: &str) -> Option<&'a MonolithRef> {
    release.linux_app.as_ref().filter(|app| needs_bootstrapper_update(running, &app.version))
}

/// Downloads `app` and swaps it in for the AppImage at `image`, which must be a file in a folder
/// the user can write. A failure leaves `image` as it was; an interrupted download resumes from its
/// `.part` next time.
pub fn replace(
    content: &dyn ContentSource,
    app: &MonolithRef,
    image: &Path,
    progress: ByteProgress<'_>,
    cancel: &Cancel,
) -> Result<()> {
    let (Some(name), Some(dir)) = (image.file_name(), image.parent()) else {
        return Err(Error::other(format!("'{}' is not an AppImage path", image.display())));
    };
    let mode = fs::metadata(image)?.permissions().mode() & 0o7777 | 0o500;
    // Hidden, and named by version: a half-done download of another version is never resumed into
    // this one.
    let mut staged = OsString::from(".");
    staged.push(name);
    staged.push(format!(".caligo-{}", app.version));
    let staged = dir.join(staged);
    log::info!("downloading AppImage {} ({} bytes)", app.version, app.artifact.size);
    content.download_to_file(&app.artifact.key, &staged, Some(&app.artifact.hash), progress, cancel)?;
    let swapped = (|| -> Result<()> {
        fs::set_permissions(&staged, fs::Permissions::from_mode(mode))?;
        cancel.check()?;
        fs::rename(&staged, image)?;
        // The rename itself, durable before anyone is told the update is in.
        fs::File::open(dir)?.sync_all()?;
        Ok(())
    })();
    if swapped.is_err() {
        let _ = fs::remove_file(&staged);
    }
    swapped?;
    log::info!("AppImage {} is in place at {}", app.version, image.display());
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::content::LocalSource;
    use crate::crypto::sha256_hex;
    use crate::manifest::ArtifactRef;

    fn release(app: Option<MonolithRef>) -> ReleaseManifest {
        serde_json::from_value(serde_json::json!({
            "schemaVersion": 1, "channel": "live", "releaseSeq": 7, "releaseId": "live-7",
            "createdAt": "2026-11-10T00:00:00Z", "packages": [],
            "linuxApp": app,
        }))
        .unwrap()
    }

    fn image_ref(bytes: &[u8], version: &str) -> MonolithRef {
        MonolithRef {
            artifact: ArtifactRef { key: format!("linuxapp/{version}/App.AppImage"), hash: sha256_hex(bytes), size: bytes.len() as u64 },
            bundle_name: "App.AppImage".into(),
            version: version.into(),
        }
    }

    #[test]
    fn only_a_newer_image_is_an_update() {
        let r = release(Some(image_ref(b"x", "3.1.3.82")));
        assert!(update_for(&r, "3.1.3.81").is_some());
        assert!(update_for(&r, "3.1.3.82").is_none());
        assert!(update_for(&r, "3.1.4.0").is_none());
        assert!(update_for(&release(None), "1.0.0").is_none());
    }

    #[test]
    fn the_image_is_replaced_with_its_mode_and_a_bad_hash_changes_nothing() {
        let cdn = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let new = b"#!/bin/sh\necho new\n";
        let good = image_ref(new, "2.0.0");
        fs::create_dir_all(cdn.path().join("linuxapp/2.0.0")).unwrap();
        fs::write(cdn.path().join(&good.artifact.key), new).unwrap();
        let image = home.path().join("App.AppImage");
        fs::write(&image, b"old").unwrap();
        fs::set_permissions(&image, fs::Permissions::from_mode(0o750)).unwrap();
        let source = LocalSource::new(cdn.path()).unwrap();

        let mut bad = good.clone();
        bad.artifact.hash = sha256_hex(b"something else");
        assert!(replace(&source, &bad, &image, None, &Cancel::new()).is_err());
        assert_eq!(fs::read(&image).unwrap(), b"old");

        replace(&source, &good, &image, None, &Cancel::new()).unwrap();
        assert_eq!(fs::read(&image).unwrap(), new);
        assert_eq!(fs::metadata(&image).unwrap().permissions().mode() & 0o7777, 0o750);
        let left: Vec<_> = fs::read_dir(home.path()).unwrap().map(|e| e.unwrap().file_name()).collect();
        assert_eq!(left, vec![OsString::from("App.AppImage")], "nothing staged is left behind");
    }
}
