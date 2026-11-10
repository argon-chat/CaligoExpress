//! `state.json`, the installation's commit point. Writes are atomic and keep a `.bak`; reads fall
//! back to the backup and then to an empty state, so a torn write never bricks the install (at worst
//! the next run repairs it).

use std::path::Path;

use crate::Result;
use crate::fs::write_atomic;
use crate::manifest::LocalInstallState;
use crate::paths::InstallPaths;

#[derive(Clone, Debug)]
pub struct StateStore {
    paths: InstallPaths,
}

impl StateStore {
    pub fn new(paths: InstallPaths) -> Self {
        Self { paths }
    }

    pub fn read(&self) -> LocalInstallState {
        read_file(&self.paths.state_file())
            .or_else(|| read_file(&self.paths.state_backup_file()))
            .unwrap_or_default()
    }

    pub fn write(&self, state: &LocalInstallState) -> Result<()> {
        self.paths.ensure_control_dirs()?;
        let json = serde_json::to_string_pretty(state)?;
        write_atomic(&self.paths.state_file(), json.as_bytes(), true)?;
        Ok(())
    }
}

fn read_file(path: &Path) -> Option<LocalInstallState> {
    let text = std::fs::read_to_string(path).ok()?;
    // The .NET writer may put a UTF-8 byte order mark first.
    serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()
}
