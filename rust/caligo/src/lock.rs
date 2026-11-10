//! A cross-process lock on one installation, so two updaters never mutate the same tree. It is the
//! .NET client's lock: `.caligo/lock` opened exclusively and deleted on close (on Windows no sharing
//! and `FILE_FLAG_DELETE_ON_CLOSE`; on Unix an `flock`), so the two clients exclude each other too.
//! A crashed holder's handle is closed by the OS and the next acquirer gets in.

use std::fs::File;
use std::io;
use std::time::{Duration, Instant};

use crate::paths::InstallPaths;
use crate::{Error, Result};

pub struct InstallLock {
    _file: File,
    #[cfg(unix)]
    path: std::path::PathBuf,
}

impl InstallLock {
    pub fn acquire(paths: &InstallPaths, timeout: Duration) -> Result<Self> {
        paths.ensure_control_dirs()?;
        let deadline = Instant::now() + timeout;
        loop {
            match Self::try_acquire(paths) {
                Ok(lock) => return Ok(lock),
                Err(e) if busy(&e) => {
                    if Instant::now() >= deadline {
                        return Err(Error::other("another updater is already running for this installation"));
                    }
                    std::thread::sleep(Duration::from_millis(50));
                }
                Err(e) => return Err(e.into()),
            }
        }
    }

    #[cfg(windows)]
    fn try_acquire(paths: &InstallPaths) -> io::Result<Self> {
        use std::os::windows::fs::OpenOptionsExt;
        const FILE_FLAG_DELETE_ON_CLOSE: u32 = 0x0400_0000;
        let file = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .share_mode(0)
            .custom_flags(FILE_FLAG_DELETE_ON_CLOSE)
            .open(paths.lock_file())?;
        Ok(Self { _file: file })
    }

    #[cfg(unix)]
    fn try_acquire(paths: &InstallPaths) -> io::Result<Self> {
        use fs2::FileExt;
        let path = paths.lock_file();
        let file = std::fs::OpenOptions::new().read(true).write(true).create(true).truncate(false).open(&path)?;
        file.try_lock_exclusive()?;
        Ok(Self { _file: file, path })
    }
}

#[cfg(unix)]
impl Drop for InstallLock {
    fn drop(&mut self) {
        // Removed while still held, so it can never delete a file another holder locked.
        let _ = std::fs::remove_file(&self.path);
    }
}

fn busy(e: &io::Error) -> bool {
    #[cfg(windows)]
    {
        // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION; a delete-pending file reads as access denied.
        matches!(e.raw_os_error(), Some(32 | 33 | 5))
    }
    #[cfg(unix)]
    {
        e.kind() == io::ErrorKind::WouldBlock || e.raw_os_error() == fs2::lock_contended_error().raw_os_error()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_second_acquirer_waits_and_then_gets_the_lock() {
        let dir = tempfile::tempdir().unwrap();
        let paths = InstallPaths::new(dir.path(), "boot.exe").unwrap();
        let first = InstallLock::acquire(&paths, Duration::from_secs(1)).unwrap();
        assert!(InstallLock::acquire(&paths, Duration::from_millis(150)).is_err());
        drop(first);
        InstallLock::acquire(&paths, Duration::from_secs(1)).unwrap();
    }
}
