use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use crate::{Error, Result};

/// A cancellation flag shared between the UI and the worker running the engine. The engine checks it
/// between reads, chunks and files; a cancelled run leaves the live tree untouched (staged work is
/// discarded or rolled back by recovery on the next run).
#[derive(Clone, Default, Debug)]
pub struct Cancel(Arc<AtomicBool>);

impl Cancel {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn cancel(&self) {
        self.0.store(true, Ordering::Relaxed);
    }

    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::Relaxed)
    }

    /// `Err(Cancelled)` once cancelled.
    pub fn check(&self) -> Result<()> {
        if self.is_cancelled() { Err(Error::Cancelled) } else { Ok(()) }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    Plan,
    Download,
    Install,
    Done,
}

/// Coarse progress for the UI: a phase, the item (`"launcher"` for the bootstrapper download, `"app"`
/// for the macOS bundle) and download bytes, summed over chunks.
#[derive(Clone, Debug, PartialEq)]
pub struct UpdateProgress {
    pub phase: Phase,
    pub item: Option<&'static str>,
    pub bytes_received: u64,
    pub total_bytes: Option<u64>,
    pub fraction: Option<f64>,
}

impl UpdateProgress {
    pub fn phase(phase: Phase) -> Self {
        Self { phase, item: None, bytes_received: 0, total_bytes: None, fraction: None }
    }

    pub fn download(item: Option<&'static str>, received: u64, total: Option<u64>) -> Self {
        let fraction = total.filter(|&t| t > 0).map(|t| received as f64 / t as f64);
        Self { phase: Phase::Download, item, bytes_received: received, total_bytes: total, fraction }
    }
}

/// Where progress goes. Called from worker threads (chunk downloads run in parallel).
pub type Progress<'a> = Option<&'a (dyn Fn(UpdateProgress) + Sync)>;
