//! SemVer 2.0 precedence, used to block bootstrapper downgrades. Release direction is the release
//! sequence's job, not this.

use std::cmp::Ordering;

#[derive(Clone, Debug)]
pub struct PackageVersion {
    pub major: u32,
    pub minor: u32,
    pub patch: u32,
    /// Dot-separated pre-release identifiers; empty for a stable release.
    pub pre: Vec<String>,
    /// Build metadata after '+', ignored for precedence.
    pub build: String,
}

impl PackageVersion {
    pub fn parse(s: &str) -> Option<Self> {
        let raw = s.trim();
        if raw.is_empty() {
            return None;
        }
        let (work, build) = match raw.split_once('+') {
            Some((work, build)) => {
                if !valid_dot_separated(build, true) {
                    return None;
                }
                (work, build)
            }
            None => (raw, ""),
        };
        let (core, pre) = match work.split_once('-') {
            Some((core, pre)) => {
                if !valid_dot_separated(pre, false) {
                    return None;
                }
                (core, pre.split('.').map(str::to_owned).collect())
            }
            None => (work, Vec::new()),
        };
        let mut parts = core.split('.');
        let (major, minor, patch) = (core_part(parts.next())?, core_part(parts.next())?, core_part(parts.next())?);
        if parts.next().is_some() {
            return None;
        }
        Some(Self { major, minor, patch, pre, build: build.to_owned() })
    }
}

fn core_part(part: Option<&str>) -> Option<u32> {
    let part = part?;
    if part.is_empty() || (part.len() > 1 && part.starts_with('0')) || !part.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    part.parse().ok()
}

fn valid_dot_separated(s: &str, allow_leading_zero_numeric: bool) -> bool {
    !s.is_empty()
        && s.split('.').all(|id| {
            !id.is_empty()
                && id.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
                && (allow_leading_zero_numeric
                    || !id.bytes().all(|b| b.is_ascii_digit())
                    || id.len() == 1
                    || !id.starts_with('0'))
        })
}

fn compare_identifier(a: &str, b: &str) -> Ordering {
    let numeric = |s: &str| s.bytes().all(|b| b.is_ascii_digit()).then(|| s.parse::<u64>().ok()).flatten();
    match (numeric(a), numeric(b)) {
        (Some(x), Some(y)) => x.cmp(&y),
        // Numeric identifiers have lower precedence than alphanumeric ones.
        (Some(_), None) => Ordering::Less,
        (None, Some(_)) => Ordering::Greater,
        (None, None) => a.cmp(b),
    }
}

impl Ord for PackageVersion {
    fn cmp(&self, other: &Self) -> Ordering {
        (self.major, self.minor, self.patch)
            .cmp(&(other.major, other.minor, other.patch))
            .then_with(|| match (self.pre.is_empty(), other.pre.is_empty()) {
                (true, true) => Ordering::Equal,
                // A pre-release sorts below the release itself.
                (true, false) => Ordering::Greater,
                (false, true) => Ordering::Less,
                (false, false) => self
                    .pre
                    .iter()
                    .zip(&other.pre)
                    .map(|(a, b)| compare_identifier(a, b))
                    .find(|o| o.is_ne())
                    .unwrap_or_else(|| self.pre.len().cmp(&other.pre.len())),
            })
    }
}

impl PartialOrd for PackageVersion {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl PartialEq for PackageVersion {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other).is_eq()
    }
}

impl Eq for PackageVersion {}

/// The release channels the control plane knows.
pub const CHANNELS: [&str; 3] = ["live", "beta", "canary"];

/// A known channel name, lowercased.
pub fn parse_channel(s: &str) -> Option<&'static str> {
    let s = s.trim().to_ascii_lowercase();
    CHANNELS.into_iter().find(|c| *c == s)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(s: &str) -> PackageVersion {
        PackageVersion::parse(s).unwrap_or_else(|| panic!("{s} should parse"))
    }

    #[test]
    fn semver_precedence_follows_the_spec_example_chain() {
        let chain = [
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
            "2.0.0",
            "2.1.0",
            "2.1.1",
        ];
        for pair in chain.windows(2) {
            assert!(v(pair[0]) < v(pair[1]), "{} < {}", pair[0], pair[1]);
        }
        assert_eq!(v("1.2.3+build.5"), v("1.2.3+other"));
    }

    #[test]
    fn malformed_versions_do_not_parse() {
        for bad in ["", "1.2", "1.2.3.4", "01.2.3", "1.2.3-", "1.2.3-01", "1.2.3+", "1.2.x", "3.1.3.68"] {
            assert!(PackageVersion::parse(bad).is_none(), "{bad}");
        }
    }
}
