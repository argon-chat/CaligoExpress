//! Resolves what to install. The small signed documents come from the seed (manifest) hosts: the
//! config, verified against the keys pinned in the binary, then the channel's release and
//! bootstrapper pointer, verified against the config's keys. Large content comes from the config's
//! mirrors, region-ordered.

use reqwest::blocking::Client;

use crate::content::{ContentSource, MirrorSource};
use crate::crypto::{KeyMap, key_map, load_bootstrapper, load_config, load_release};
use crate::keys::KeyScheme;
use crate::manifest::{BootstrapperManifest, ConfigManifest, ReleaseManifest};
use crate::{Cancel, Error, Result};

#[derive(Clone, Debug)]
pub struct ClientOptions {
    /// Hard-coded hosts that serve the signed documents before any mirror is known.
    pub seed_hosts: Vec<String>,
    /// Trust anchors pinned in the binary.
    pub pinned_keys: KeyMap,
    pub scheme: String,
    /// ISO 3166 two-letter region: mirrors for it are tried first.
    pub region: Option<String>,
    /// The app slug on a multi-app worker; `None` for the default app.
    pub app_id: Option<String>,
    /// Off only in development: production must verify.
    pub require_signatures: bool,
}

pub struct ResolvedRelease {
    /// Region-ordered content mirrors.
    pub content: MirrorSource,
    pub keys: KeyScheme,
    pub config: ConfigManifest,
    pub release: ReleaseManifest,
    /// `None` when the channel has no bootstrapper pointer yet: no self-update then.
    pub bootstrapper: Option<BootstrapperManifest>,
}

pub fn resolve(options: &ClientOptions, http: &Client, channel: &str, cancel: &Cancel) -> Result<ResolvedRelease> {
    let manifests = MirrorSource::http(http, &options.scheme, &options.seed_hosts)?;
    let keys = KeyScheme::for_app(options.app_id.as_deref());
    let verify = options.require_signatures;

    let config_bytes = manifests.get_bytes(&keys.config(), cancel)?;
    let config = load_config(&config_bytes, verify.then_some(&options.pinned_keys))?;
    let config_keys = key_map(&config.public_keys);

    let release_key = config.channels.get(channel).cloned().unwrap_or_else(|| keys.release(channel));
    let release_bytes = manifests.get_bytes(&release_key, cancel)?;
    let release = load_release(&release_bytes, verify.then_some(&config_keys))?;

    let bootstrapper = match manifests.get_bytes(&keys.bootstrapper_manifest(channel), cancel) {
        Ok(bytes) => Some(load_bootstrapper(&bytes, verify.then_some(&config_keys))?),
        // Only a 404 everywhere means "none": a mirror outage must not pass for "no update".
        Err(Error::NotFound { source, .. }) if source.as_deref().is_none_or(|e| matches!(e, Error::NotFound { .. })) => {
            log::info!("no bootstrapper.json for channel {channel}; self-update skipped");
            None
        }
        Err(e) => return Err(e),
    };

    let content = MirrorSource::from_config(http, &config.mirrors, options.region.as_deref())?;
    Ok(ResolvedRelease { content, keys, config, release, bootstrapper })
}
