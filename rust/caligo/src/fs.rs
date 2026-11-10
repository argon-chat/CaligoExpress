//! Manifest paths, validated before they touch the disk, and crash-safe writes and moves.
//!
//! Destructive operations retry briefly: on Windows an antivirus or the indexer often holds a file
//! that was just written for a moment.

use std::fs::{self, File};
use std::io::{self, Write};
use std::path::{Component, Path, PathBuf};
use std::time::Duration;

use crate::{Error, Result};

const BACKOFF_MS: [u64; 8] = [10, 25, 50, 100, 150, 200, 300, 500];

/// A manifest path normalized to forward-slash relative form. Rejects anything that could escape a
/// root: absolute, drive-qualified, empty, `.` or `..` segments, NULs.
pub fn normalize_relative(path: &str) -> Result<String> {
    let p = path.replace('\\', "/");
    let p = p.trim();
    let unsafe_path = || Error::UnsafePath(path.to_owned());
    if p.is_empty() || p.starts_with('/') || p.as_bytes().get(1) == Some(&b':') || p.contains('\0') {
        return Err(unsafe_path());
    }
    if p.split('/').any(|seg| seg.is_empty() || seg == "." || seg == "..") {
        return Err(unsafe_path());
    }
    Ok(p.to_owned())
}

/// `relative` resolved under `root`, guaranteed to stay inside it.
pub fn resolve_under(root: &Path, relative: &str) -> Result<PathBuf> {
    let norm = normalize_relative(relative)?;
    let mut full = absolute(root)?;
    for seg in norm.split('/') {
        // A segment can still smuggle a root or prefix in on some platforms ("C:" was rejected above,
        // but e.g. a lone component that parses as a prefix must not replace the root).
        let mut components = Path::new(seg).components();
        match (components.next(), components.next()) {
            (Some(Component::Normal(name)), None) => full.push(name),
            _ => return Err(Error::UnsafePath(relative.to_owned())),
        }
    }
    Ok(full)
}

/// An absolute, lexically normalized path (no filesystem access, like `Path.GetFullPath`).
pub fn absolute(path: &Path) -> Result<PathBuf> {
    let joined = if path.is_absolute() { path.to_path_buf() } else { std::env::current_dir()?.join(path) };
    let mut out = PathBuf::new();
    for c in joined.components() {
        match c {
            Component::ParentDir => {
                out.pop();
            }
            Component::CurDir => {}
            c => out.push(c),
        }
    }
    Ok(out)
}

fn retry<T>(mut op: impl FnMut() -> io::Result<T>) -> io::Result<T> {
    let mut attempt = 0;
    loop {
        match op() {
            Ok(v) => return Ok(v),
            Err(e) if attempt < BACKOFF_MS.len() && transient(&e) => {
                std::thread::sleep(Duration::from_millis(BACKOFF_MS[attempt]));
                attempt += 1;
            }
            Err(e) => return Err(e),
        }
    }
}

fn transient(e: &io::Error) -> bool {
    !matches!(e.kind(), io::ErrorKind::NotFound | io::ErrorKind::AlreadyExists | io::ErrorKind::InvalidInput)
}

fn clear_readonly(path: &Path) {
    if let Ok(meta) = fs::metadata(path) {
        let mut perms = meta.permissions();
        if perms.readonly() {
            #[allow(clippy::permissions_set_readonly_false)]
            perms.set_readonly(false);
            let _ = fs::set_permissions(path, perms);
        }
    }
}

fn ensure_parent(path: &Path) -> io::Result<()> {
    match path.parent() {
        Some(dir) if !dir.as_os_str().is_empty() => fs::create_dir_all(dir),
        _ => Ok(()),
    }
}

/// Writes `bytes` into a fresh file and flushes it to disk.
pub fn write_synced(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let mut file = File::create(path)?;
    file.write_all(bytes)?;
    file.sync_all()
}

fn tmp_sibling(path: &Path) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(format!(".tmp-{}", random_hex(16)));
    PathBuf::from(name)
}

/// Writes `bytes` to a flushed temp sibling, then renames it over `path`, so a reader sees the old
/// file or the new one, never a torn one. With `keep_backup` the previous content is first copied
/// to `path.bak`.
pub fn write_atomic(path: &Path, bytes: &[u8], keep_backup: bool) -> io::Result<()> {
    ensure_parent(path)?;
    let tmp = tmp_sibling(path);
    write_synced(&tmp, bytes)?;
    if path.exists() {
        clear_readonly(path);
        if keep_backup {
            let mut bak = path.as_os_str().to_owned();
            bak.push(".bak");
            let bak = PathBuf::from(bak);
            clear_readonly(&bak);
            retry(|| fs::copy(path, &bak))?;
        }
    }
    let renamed = retry(|| fs::rename(&tmp, path));
    if renamed.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    renamed
}

/// A move that replaces an existing destination and rides out transient locks.
pub fn move_file(source: &Path, destination: &Path) -> io::Result<()> {
    ensure_parent(destination)?;
    if destination.exists() {
        clear_readonly(destination);
    }
    retry(|| fs::rename(source, destination))
}

pub fn delete_file(path: &Path) -> io::Result<()> {
    if !path.exists() {
        return Ok(());
    }
    clear_readonly(path);
    retry(|| match fs::remove_file(path) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        r => r,
    })
}

/// Moves a directory tree (a macOS bundle) into place: one rename on the same volume. An existing
/// destination is removed first when `overwrite`.
pub fn move_dir(source: &Path, destination: &Path, overwrite: bool) -> io::Result<()> {
    ensure_parent(destination)?;
    if overwrite && destination.is_dir() {
        delete_dir(destination)?;
    }
    retry(|| fs::rename(source, destination))
}

pub fn delete_dir(path: &Path) -> io::Result<()> {
    if !path.exists() {
        return Ok(());
    }
    retry(|| match fs::remove_dir_all(path) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        r => r,
    })
}

/// Random lowercase hex from the OS generator.
pub fn random_hex(bytes: usize) -> String {
    use ring::rand::{SecureRandom, SystemRandom};
    let mut buf = vec![0u8; bytes];
    SystemRandom::new().fill(&mut buf).expect("the OS random generator failed");
    crate::crypto::hex(&buf)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn traversal_and_absolute_paths_are_rejected() {
        for bad in ["", "  ", "/etc/passwd", "\\x", "C:/x", "c:x", "a/../b", "./a", "a//b", "a/", "..", "a\0b"] {
            assert!(normalize_relative(bad).is_err(), "{bad:?}");
        }
        assert_eq!(normalize_relative("app\\resources\\x.pak").unwrap(), "app/resources/x.pak");
    }

    #[test]
    fn a_resolved_path_stays_under_the_root() {
        let root = std::env::temp_dir().join("caligo-root");
        let p = resolve_under(&root, "app/x.exe").unwrap();
        assert!(p.starts_with(absolute(&root).unwrap()));
        assert!(resolve_under(&root, "../x").is_err());
    }

    #[test]
    fn an_atomic_write_keeps_the_previous_content_as_a_backup() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("state.json");
        write_atomic(&path, b"one", true).unwrap();
        write_atomic(&path, b"two", true).unwrap();
        assert_eq!(fs::read(&path).unwrap(), b"two");
        assert_eq!(fs::read(dir.path().join("state.json.bak")).unwrap(), b"one");
        assert_eq!(fs::read_dir(dir.path()).unwrap().count(), 2, "no temp file is left behind");
    }
}
