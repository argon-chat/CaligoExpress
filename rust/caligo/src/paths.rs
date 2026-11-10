//! The on-disk layout of an installation. Everything the updater owns lives in one `.caligo` control
//! directory, so the app tree stays clean.

use std::io;
use std::path::{Path, PathBuf};

use crate::Result;
use crate::fs::{absolute, normalize_relative, resolve_under};

#[derive(Clone, Debug)]
pub struct InstallPaths {
    root: PathBuf,
    bootstrapper_file_name: String,
    control_dir: PathBuf,
    bootstrapper: PathBuf,
}

impl InstallPaths {
    /// `root` is where package target roots and the launch bin resolve (Windows `%AppData%\.my-app`,
    /// macOS `~/Library/Application Support/MyApp`). The bootstrapper defaults to
    /// `root/<bootstrapper_file_name>`; on macOS its bundle lives elsewhere (see [`Self::with_bootstrapper`]).
    pub fn new(root: impl AsRef<Path>, bootstrapper_file_name: impl Into<String>) -> Result<Self> {
        let root = absolute(root.as_ref())?;
        let bootstrapper_file_name = bootstrapper_file_name.into();
        Ok(Self {
            control_dir: root.join(".caligo"),
            bootstrapper: root.join(&bootstrapper_file_name),
            root,
            bootstrapper_file_name,
        })
    }

    /// The bootstrapper's own location, when it is not under the root.
    pub fn with_bootstrapper(mut self, path: impl AsRef<Path>) -> Result<Self> {
        self.bootstrapper = absolute(path.as_ref())?;
        Ok(self)
    }

    /// The control directory, when it is not `root/.caligo`.
    pub fn with_control_dir(mut self, path: impl AsRef<Path>) -> Result<Self> {
        self.control_dir = absolute(path.as_ref())?;
        Ok(self)
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn bootstrapper_file_name(&self) -> &str {
        &self.bootstrapper_file_name
    }

    pub fn control_dir(&self) -> &Path {
        &self.control_dir
    }

    pub fn state_file(&self) -> PathBuf {
        self.control_dir.join("state.json")
    }

    pub fn state_backup_file(&self) -> PathBuf {
        self.control_dir.join("state.json.bak")
    }

    pub fn journal_dir(&self) -> PathBuf {
        self.control_dir.join("journal")
    }

    pub fn cache_dir(&self) -> PathBuf {
        self.control_dir.join("cache")
    }

    pub fn chunk_cache_dir(&self) -> PathBuf {
        self.cache_dir().join("chunks")
    }

    pub fn chunk_cache_file(&self, hash: &str) -> PathBuf {
        self.chunk_cache_dir().join(&hash[..2.min(hash.len())]).join(hash)
    }

    pub fn lock_file(&self) -> PathBuf {
        self.control_dir.join("lock")
    }

    /// The running bootstrapper's install location (an `.exe`, or a `.app` directory on macOS).
    pub fn bootstrapper_exe(&self) -> &Path {
        &self.bootstrapper
    }

    pub fn bootstrapper_new(&self) -> PathBuf {
        self.control_dir.join(format!("{}.new", self.bootstrapper_file_name))
    }

    pub fn bootstrapper_old(&self) -> PathBuf {
        self.control_dir.join(format!("{}.old", self.bootstrapper_file_name))
    }

    pub fn txn_dir(&self, id: &str) -> PathBuf {
        self.journal_dir().join(id)
    }

    pub fn txn_journal_file(&self, id: &str) -> PathBuf {
        self.txn_dir(id).join("txn.json")
    }

    pub fn txn_staged_dir(&self, id: &str) -> PathBuf {
        self.txn_dir(id).join("staged")
    }

    pub fn txn_backup_dir(&self, id: &str) -> PathBuf {
        self.txn_dir(id).join("backup")
    }

    pub fn ensure_control_dirs(&self) -> io::Result<()> {
        std::fs::create_dir_all(self.journal_dir())?;
        std::fs::create_dir_all(self.cache_dir())
    }

    /// A package file's install location, validated to stay under the root.
    pub fn installed_file(&self, target_root: &str, relative: &str) -> Result<PathBuf> {
        let combined = format!("{}/{}", normalize_relative(target_root)?, normalize_relative(relative)?);
        resolve_under(&self.root, &combined)
    }

    /// A root-relative path (a launch bin like `app/MyApp.exe`), validated to stay under the root.
    pub fn under_root(&self, relative: &str) -> Result<PathBuf> {
        resolve_under(&self.root, relative)
    }
}
