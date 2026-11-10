//! The CaligoExpress client engine, ported from the .NET library (`src/CaligoExpress`).
//!
//! Trust chains a key pinned in the binary → the signed `config.json` → the signed `release.json` and
//! `bootstrapper.json` → the SHA-256 of every package manifest, chunk and file; mirrors are untrusted.
//! On disk it shares the .NET client's layout byte for byte (`.caligo/state.json`, the transaction
//! journals, the chunk cache, the install lock), so either client can take over an installation the
//! other left, an interrupted update included.
//!
//! Everything here blocks: run it on a worker thread. Long operations take a [`Cancel`] and report
//! [`UpdateProgress`] through a callback.

#[cfg(unix)]
pub mod appimage;
pub mod bootstrap;
pub mod chunk;
pub mod client;
pub mod content;
pub mod crypto;
mod error;
pub mod fs;
pub mod hooks;
pub mod keys;
pub mod lock;
pub mod manifest;
pub mod monolith;
pub mod paths;
pub mod planner;
mod progress;
pub mod self_update;
pub mod state;
pub mod txn;
pub mod update;
pub mod version;

pub use error::{Error, Result};
pub use progress::{Cancel, Phase, Progress, UpdateProgress};

/// True where the app is delivered as one signed `.app` bundle (the monolith path) and the
/// bootstrapper is a bundle too; everywhere else the chunked package path runs.
pub const IS_MAC: bool = cfg!(target_os = "macos");
