//! Fetching by key: one HTTP mirror, a CDN-shaped local directory, and failover across mirrors.
//! Sources own transport; callers own trust (hashes and signatures), though a download can verify
//! an expected hash close to the wire.

use std::collections::HashSet;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Duration;

use reqwest::StatusCode;
use reqwest::blocking::{Client, Response};
use reqwest::header::{CONTENT_LENGTH, RANGE};

use crate::crypto::{Sha256, expect_hash, sha256_file};
use crate::fs::{absolute, move_file, resolve_under};
use crate::manifest::MirrorEntry;
use crate::{Cancel, Error, Result};

/// Bytes received so far and the total when known, for one download.
pub type ByteProgress<'a> = Option<&'a (dyn Fn(u64, Option<u64>) + Sync)>;

pub trait ContentSource: Send + Sync {
    /// A small document (a manifest), fully in memory.
    fn get_bytes(&self, key: &str, cancel: &Cancel) -> Result<Vec<u8>>;

    /// A possibly large artifact into `destination`. With `expected_sha256` the result is verified and
    /// a mismatch fails with [`Error::HashMismatch`], leaving nothing at `destination`.
    fn download_to_file(
        &self,
        key: &str,
        destination: &Path,
        expected_sha256: Option<&str>,
        progress: ByteProgress<'_>,
        cancel: &Cancel,
    ) -> Result<()>;
}

/// No answer within this long, or no body bytes for this long, fails the mirror over.
pub const STALL_TIMEOUT: Duration = Duration::from_secs(12);

/// The client every HTTP mirror shares. Its timeout bounds the wait for response headers and each
/// body read separately, so a mirror that accepts the connection and then says nothing fails over
/// instead of hanging, while a long healthy download is never cut off.
pub fn http_client() -> Result<Client> {
    Client::builder()
        .connect_timeout(Duration::from_secs(10))
        .timeout(STALL_TIMEOUT)
        .user_agent(concat!("caligo/", env!("CARGO_PKG_VERSION")))
        .build()
        .map_err(|e| http_error("could not create the HTTP client", e))
}

fn http_error(what: &str, e: reqwest::Error) -> Error {
    let mut message = format!("{what}: {e}");
    let mut source = std::error::Error::source(&e);
    while let Some(inner) = source {
        message.push_str(": ");
        message.push_str(&inner.to_string());
        source = inner.source();
    }
    Error::Http(message)
}

/// One HTTP mirror.
pub struct HttpSource {
    client: Client,
    base: String,
}

impl HttpSource {
    pub fn new(client: Client, scheme: &str, host: &str) -> Self {
        Self { client, base: format!("{scheme}://{host}").trim_end_matches('/').to_owned() }
    }

    fn url(&self, key: &str) -> String {
        format!("{}/{}", self.base, key.trim_start_matches('/'))
    }

    fn get(&self, key: &str, range_from: u64) -> Result<Response> {
        let url = self.url(key);
        let mut request = self.client.get(&url);
        if range_from > 0 {
            request = request.header(RANGE, format!("bytes={range_from}-"));
        }
        let response = request.send().map_err(|e| http_error(&format!("GET {url}"), e))?;
        match response.status() {
            StatusCode::NOT_FOUND => Err(Error::NotFound { key: key.into(), source: None }),
            s if s.is_success() => Ok(response),
            s => Err(Error::Status { url, status: s.as_u16() }),
        }
    }

    fn read_some(&self, key: &str, body: &mut Response, buf: &mut [u8]) -> Result<usize> {
        body.read(buf)
            .map_err(|e| Error::Http(format!("download from {} stalled or failed for '{key}': {e}", self.base)))
    }
}

impl ContentSource for HttpSource {
    fn get_bytes(&self, key: &str, cancel: &Cancel) -> Result<Vec<u8>> {
        let mut response = self.get(key, 0)?;
        let mut out = Vec::with_capacity(response.content_length().unwrap_or(0).min(64 << 20) as usize);
        let mut buf = vec![0u8; 80 * 1024];
        loop {
            cancel.check()?;
            let n = self.read_some(key, &mut response, &mut buf)?;
            if n == 0 {
                return Ok(out);
            }
            out.extend_from_slice(&buf[..n]);
        }
    }

    fn download_to_file(
        &self,
        key: &str,
        destination: &Path,
        expected_sha256: Option<&str>,
        progress: ByteProgress<'_>,
        cancel: &Cancel,
    ) -> Result<()> {
        let part = part_path(destination);
        if let Some(dir) = part.parent() {
            fs::create_dir_all(dir)?;
        }
        let existing = fs::metadata(&part).map(|m| m.len()).unwrap_or(0);
        let mut response = self.get(key, existing)?;

        // A server that ignored the range sent the whole file: start the part over.
        let append = existing > 0 && response.status() == StatusCode::PARTIAL_CONTENT;
        let length = response
            .headers()
            .get(CONTENT_LENGTH)
            .and_then(|v| v.to_str().ok()?.parse::<u64>().ok());
        let total = length.map(|l| l + if append { existing } else { 0 });
        let mut file = if append {
            OpenOptions::new().append(true).open(&part)?
        } else {
            File::create(&part)?
        };

        let mut received = if append { existing } else { 0 };
        let mut buf = vec![0u8; 80 * 1024];
        loop {
            cancel.check()?;
            let n = self.read_some(key, &mut response, &mut buf)?;
            if n == 0 {
                break;
            }
            file.write_all(&buf[..n])?;
            received += n as u64;
            if let Some(report) = progress {
                report(received, total);
            }
        }
        file.sync_all()?;
        drop(file);

        if let Some(expected) = expected_sha256 {
            let actual = sha256_file(&part)?;
            if let Err(e) = expect_hash(key, expected, &actual) {
                // A poisoned part: the next mirror starts clean.
                let _ = fs::remove_file(&part);
                return Err(e);
            }
        }
        move_file(&part, destination)?;
        Ok(())
    }
}

fn part_path(destination: &Path) -> PathBuf {
    let mut name = destination.as_os_str().to_owned();
    name.push(".part");
    PathBuf::from(name)
}

/// A local directory laid out like the CDN (key = relative path): the engine runs against it offline.
pub struct LocalSource {
    root: PathBuf,
}

impl LocalSource {
    pub fn new(root: impl AsRef<Path>) -> Result<Self> {
        Ok(Self { root: absolute(root.as_ref())? })
    }

    fn resolve(&self, key: &str) -> Result<PathBuf> {
        let path = resolve_under(&self.root, key)?;
        if path.is_file() { Ok(path) } else { Err(Error::NotFound { key: key.into(), source: None }) }
    }
}

impl ContentSource for LocalSource {
    fn get_bytes(&self, key: &str, cancel: &Cancel) -> Result<Vec<u8>> {
        cancel.check()?;
        Ok(fs::read(self.resolve(key)?)?)
    }

    fn download_to_file(
        &self,
        key: &str,
        destination: &Path,
        expected_sha256: Option<&str>,
        progress: ByteProgress<'_>,
        cancel: &Cancel,
    ) -> Result<()> {
        let mut source = File::open(self.resolve(key)?)?;
        let total = source.metadata()?.len();
        if let Some(dir) = destination.parent() {
            fs::create_dir_all(dir)?;
        }
        let mut dest = File::create(destination)?;
        let mut hasher = Sha256::new();
        let mut buf = vec![0u8; 80 * 1024];
        let mut received = 0u64;
        loop {
            cancel.check()?;
            let n = source.read(&mut buf)?;
            if n == 0 {
                break;
            }
            dest.write_all(&buf[..n])?;
            hasher.update(&buf[..n]);
            received += n as u64;
            if let Some(report) = progress {
                report(received, Some(total));
            }
        }
        dest.sync_all()?;
        drop(dest);
        if let Some(expected) = expected_sha256
            && let Err(e) = expect_hash(key, expected, &hasher.finish_hex())
        {
            let _ = fs::remove_file(destination);
            return Err(e);
        }
        Ok(())
    }
}

/// Failover across an ordered set of mirrors. A mirror that fails (refused, silent, an error status)
/// is penalized: moved to the back for the rest of the session, so a dead mirror costs one request,
/// not one per chunk. A 404 is about the key, not the mirror, so it fails over without a penalty.
/// Everything is content-addressed, so mixing mirrors is safe.
pub struct MirrorSource {
    mirrors: Vec<Box<dyn ContentSource>>,
    penalized: Mutex<HashSet<usize>>,
}

impl MirrorSource {
    pub fn new(mirrors: Vec<Box<dyn ContentSource>>) -> Result<Self> {
        if mirrors.is_empty() {
            return Err(Error::other("at least one mirror is required"));
        }
        Ok(Self { mirrors, penalized: Mutex::new(HashSet::new()) })
    }

    /// HTTP mirrors on `hosts`, in order.
    pub fn http(client: &Client, scheme: &str, hosts: &[String]) -> Result<Self> {
        Self::new(
            hosts
                .iter()
                .map(|h| Box::new(HttpSource::new(client.clone(), scheme, h)) as Box<dyn ContentSource>)
                .collect(),
        )
    }

    /// The config's mirrors, region-ordered (see [`order_mirrors`]).
    pub fn from_config(client: &Client, mirrors: &[MirrorEntry], region: Option<&str>) -> Result<Self> {
        let ordered = order_mirrors(mirrors, region);
        if ordered.is_empty() {
            return Err(Error::other("config contains no usable mirrors"));
        }
        Self::new(
            ordered
                .into_iter()
                .map(|m| Box::new(HttpSource::new(client.clone(), &m.scheme, &m.host)) as Box<dyn ContentSource>)
                .collect(),
        )
    }

    fn order(&self) -> Vec<usize> {
        let penalized = self.penalized.lock().unwrap();
        let (healthy, bad): (Vec<usize>, Vec<usize>) = (0..self.mirrors.len()).partition(|i| !penalized.contains(i));
        healthy.into_iter().chain(bad).collect()
    }

    fn try_each<T>(&self, key: &str, mut fetch: impl FnMut(&dyn ContentSource) -> Result<T>) -> Result<T> {
        let mut last = None;
        for i in self.order() {
            match fetch(self.mirrors[i].as_ref()) {
                Ok(v) => return Ok(v),
                Err(Error::Cancelled) => return Err(Error::Cancelled),
                Err(e @ Error::NotFound { .. }) => last = Some(e),
                Err(e) => {
                    log::warn!("mirror {i} failed for {key}; penalizing it and trying the next: {e}");
                    self.penalized.lock().unwrap().insert(i);
                    last = Some(e);
                }
            }
        }
        Err(Error::NotFound { key: key.into(), source: last.map(Box::new) })
    }
}

impl ContentSource for MirrorSource {
    fn get_bytes(&self, key: &str, cancel: &Cancel) -> Result<Vec<u8>> {
        self.try_each(key, |m| m.get_bytes(key, cancel))
    }

    fn download_to_file(
        &self,
        key: &str,
        destination: &Path,
        expected_sha256: Option<&str>,
        progress: ByteProgress<'_>,
        cancel: &Cancel,
    ) -> Result<()> {
        self.try_each(key, |m| m.download_to_file(key, destination, expected_sha256, progress, cancel))
    }
}

/// Mirrors for `region` first (an exact region match, then unscoped or `*` ones, then those scoped to
/// other regions as a last resort), then by declared priority, then by host.
pub fn order_mirrors(mirrors: &[MirrorEntry], region: Option<&str>) -> Vec<MirrorEntry> {
    let region = region.map(|r| r.trim().to_ascii_uppercase());
    let rank = |m: &MirrorEntry| match &m.regions {
        None => 1,
        Some(rs) if rs.is_empty() => 1,
        Some(rs) if region.as_deref().is_some_and(|r| rs.iter().any(|x| x.eq_ignore_ascii_case(r))) => 2,
        Some(rs) if rs.iter().any(|x| x == "*") => 1,
        Some(_) => 0,
    };
    let mut out = mirrors.to_vec();
    out.sort_by(|a, b| rank(b).cmp(&rank(a)).then(a.priority.cmp(&b.priority)).then_with(|| a.host.cmp(&b.host)));
    out
}


#[cfg(test)]
mod tests {
    use super::*;

    fn mirror(host: &str, regions: Option<&[&str]>, priority: i32) -> MirrorEntry {
        MirrorEntry {
            host: host.into(),
            regions: regions.map(|r| r.iter().map(|s| s.to_string()).collect()),
            priority,
            scheme: "https".into(),
        }
    }

    #[test]
    fn mirrors_for_the_region_come_first_then_by_priority() {
        let mirrors = [
            mirror("eu", Some(&["*"]), 0),
            mirror("us", Some(&["US", "CA", "*"]), 1),
            mirror("ru", Some(&["RU", "BY", "*"]), 2),
            mirror("cn", Some(&["CN"]), 0),
        ];
        let hosts = |r| order_mirrors(&mirrors, r).into_iter().map(|m| m.host).collect::<Vec<_>>();
        assert_eq!(hosts(Some("ru")), ["ru", "eu", "us", "cn"]);
        assert_eq!(hosts(None), ["eu", "us", "ru", "cn"]);
    }

    struct Failing(Error);
    impl ContentSource for Failing {
        fn get_bytes(&self, key: &str, _: &Cancel) -> Result<Vec<u8>> {
            Err(match &self.0 {
                Error::NotFound { .. } => Error::NotFound { key: key.into(), source: None },
                _ => Error::Http("down".into()),
            })
        }
        fn download_to_file(&self, _: &str, _: &Path, _: Option<&str>, _: ByteProgress<'_>, _: &Cancel) -> Result<()> {
            unreachable!()
        }
    }

    struct Serving(&'static [u8]);
    impl ContentSource for Serving {
        fn get_bytes(&self, _: &str, _: &Cancel) -> Result<Vec<u8>> {
            Ok(self.0.to_vec())
        }
        fn download_to_file(&self, _: &str, _: &Path, _: Option<&str>, _: ByteProgress<'_>, _: &Cancel) -> Result<()> {
            unreachable!()
        }
    }

    #[test]
    fn a_failing_mirror_is_penalized_but_a_404_is_not() {
        let source = MirrorSource::new(vec![
            Box::new(Failing(Error::Http(String::new()))),
            Box::new(Failing(Error::NotFound { key: String::new(), source: None })),
            Box::new(Serving(b"ok")),
        ])
        .unwrap();
        let cancel = Cancel::new();
        assert_eq!(source.get_bytes("k", &cancel).unwrap(), b"ok");
        assert_eq!(source.order(), [1, 2, 0]);
    }
}
