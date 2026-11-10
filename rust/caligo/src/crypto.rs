//! Canonical JSON, Ed25519 and SHA-256: what a signature covers and how content is addressed.
//!
//! A signature covers the canonical form of the document *as received*, without its top-level
//! `signature`: keys sorted ordinally, no whitespace, integers verbatim, strings escaped as the .NET
//! signer escapes them (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`: quote, backslash, controls,
//! U+2028/U+2029 and characters outside the BMP, with uppercase hex). So fields this client does not
//! know still count, and a mirror cannot add one unnoticed.

use std::collections::BTreeMap;
use std::fmt::Write as _;
use std::io::Read;
use std::path::Path;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use ring::digest;
use ring::signature::{ED25519, Ed25519KeyPair, UnparsedPublicKey};
use serde::Serialize;
use serde::de::DeserializeOwned;
use serde_json::Value;

use crate::manifest::{BootstrapperManifest, ConfigManifest, PublicKeyEntry, ReleaseManifest, SignatureEnvelope};
use crate::{Error, Result};

pub const ALGORITHM: &str = "ed25519";

/// Trust anchors: key id → raw 32-byte Ed25519 public key.
pub type KeyMap = BTreeMap<String, Vec<u8>>;

/// The canonical bytes of a JSON value.
pub fn canonicalize(value: &Value) -> Vec<u8> {
    let mut out = String::new();
    write_value(&mut out, value);
    out.into_bytes()
}

/// The canonical bytes of `json` with the top-level `signature` removed: what gets signed.
pub fn canonicalize_for_signing(json: &str) -> Result<Vec<u8>> {
    let mut value: Value = serde_json::from_str(json)?;
    if let Value::Object(map) = &mut value {
        map.remove("signature");
    }
    Ok(canonicalize(&value))
}

fn write_value(out: &mut String, value: &Value) {
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        // Manifests carry integers only, whose text round-trips exactly.
        Value::Number(n) => out.push_str(&n.to_string()),
        Value::String(s) => write_string(out, s),
        Value::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                write_value(out, item);
            }
            out.push(']');
        }
        Value::Object(map) => {
            // Ordinal (UTF-16 code unit) order, as the .NET signer sorts. It matches byte order for
            // everything but astral characters against U+E000..U+FFFF, which no manifest key uses.
            // Sorted here, never by the map: another crate in the graph may turn on preserve_order.
            let mut entries: Vec<_> = map.iter().collect();
            entries.sort_by(|a, b| a.0.encode_utf16().cmp(b.0.encode_utf16()));
            out.push('{');
            for (i, (key, item)) in entries.into_iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                write_string(out, key);
                out.push(':');
                write_value(out, item);
            }
            out.push('}');
        }
    }
}

fn write_string(out: &mut String, s: &str) {
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 || ('\u{7f}'..='\u{9f}').contains(&c) || c == '\u{2028}' || c == '\u{2029}' => {
                let _ = write!(out, "\\u{:04X}", c as u32);
            }
            c if (c as u32) > 0xFFFF => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    let _ = write!(out, "\\u{unit:04X}");
                }
            }
            c => out.push(c),
        }
    }
    out.push('"');
}

/// Signs `json` (its canonical form, minus any signature) with a raw 32-byte Ed25519 seed. For tests
/// and tooling; clients only verify.
pub fn sign(json: &str, key_id: &str, seed: &[u8]) -> Result<SignatureEnvelope> {
    let pair = Ed25519KeyPair::from_seed_unchecked(seed).map_err(|_| Error::other("invalid ed25519 seed"))?;
    let sig = pair.sign(&canonicalize_for_signing(json)?);
    Ok(SignatureEnvelope { key_id: key_id.into(), alg: ALGORITHM.into(), sig: BASE64.encode(sig.as_ref()) })
}

/// The raw public key of a 32-byte Ed25519 seed.
pub fn public_key_from_seed(seed: &[u8]) -> Result<Vec<u8>> {
    use ring::signature::KeyPair as _;
    let pair = Ed25519KeyPair::from_seed_unchecked(seed).map_err(|_| Error::other("invalid ed25519 seed"))?;
    Ok(pair.public_key().as_ref().to_vec())
}

/// Verifies `json` against `signature` with the anchors in `keys`. False on any problem: no
/// signature, another algorithm, an unknown key, bad base64, a bad signature.
pub fn verify(json: &str, signature: Option<&SignatureEnvelope>, keys: &KeyMap) -> bool {
    let Some(signature) = signature.filter(|s| s.alg == ALGORITHM) else {
        return false;
    };
    let Some(public_key) = keys.get(&signature.key_id) else {
        return false;
    };
    let Ok(sig) = BASE64.decode(&signature.sig) else {
        return false;
    };
    let Ok(payload) = canonicalize_for_signing(json) else {
        return false;
    };
    UnparsedPublicKey::new(&ED25519, public_key).verify(&payload, &sig).is_ok()
}

/// The id → key map of a config's Ed25519 anchors.
pub fn key_map(keys: &[PublicKeyEntry]) -> KeyMap {
    keys.iter()
        .filter(|k| k.alg == ALGORITHM)
        .filter_map(|k| Some((k.id.clone(), BASE64.decode(&k.key).ok()?)))
        .collect()
}

/// Pinned keys as an installer embeds them: id → base64.
pub fn decode_pinned(keys: &BTreeMap<String, String>) -> Result<KeyMap> {
    keys.iter()
        .map(|(id, b64)| {
            let key = BASE64.decode(b64).map_err(|_| Error::other(format!("pinned key '{id}' is not base64")))?;
            Ok((id.clone(), key))
        })
        .collect()
}

trait Signed {
    const WHAT: &'static str;
    fn signature(&self) -> Option<&SignatureEnvelope>;
}

impl Signed for ConfigManifest {
    const WHAT: &'static str = "config manifest";
    fn signature(&self) -> Option<&SignatureEnvelope> {
        self.signature.as_ref()
    }
}

impl Signed for ReleaseManifest {
    const WHAT: &'static str = "release manifest";
    fn signature(&self) -> Option<&SignatureEnvelope> {
        self.signature.as_ref()
    }
}

impl Signed for BootstrapperManifest {
    const WHAT: &'static str = "bootstrapper manifest";
    fn signature(&self) -> Option<&SignatureEnvelope> {
        self.signature.as_ref()
    }
}

fn load<T: DeserializeOwned + Signed>(bytes: &[u8], keys: Option<&KeyMap>) -> Result<T> {
    let text = std::str::from_utf8(bytes).map_err(|_| Error::other(format!("{} is not UTF-8", T::WHAT)))?;
    let doc: T = serde_json::from_str(text)?;
    if let Some(keys) = keys
        && !verify(text, doc.signature(), keys)
    {
        return Err(Error::Signature(T::WHAT));
    }
    Ok(doc)
}

/// Parses config.json, verified against the pinned keys (`None` skips verification: dev only).
pub fn load_config(bytes: &[u8], pinned: Option<&KeyMap>) -> Result<ConfigManifest> {
    load(bytes, pinned)
}

/// Parses release.json, verified against the config's keys.
pub fn load_release(bytes: &[u8], keys: Option<&KeyMap>) -> Result<ReleaseManifest> {
    load(bytes, keys)
}

/// Parses bootstrapper.json, verified against the config's keys.
pub fn load_bootstrapper(bytes: &[u8], keys: Option<&KeyMap>) -> Result<BootstrapperManifest> {
    load(bytes, keys)
}

/// `value` serialized and signed: the producer side, for tests and tooling.
pub fn signed_json<T: Serialize>(value: &T, key_id: &str, seed: &[u8]) -> Result<String> {
    let Value::Object(mut map) = serde_json::to_value(value)? else {
        return Err(Error::other("only an object can be signed"));
    };
    map.remove("signature");
    let envelope = sign(&serde_json::to_string(&map)?, key_id, seed)?;
    map.insert("signature".into(), serde_json::to_value(envelope)?);
    Ok(serde_json::to_string(&map)?)
}

pub fn hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        let _ = write!(s, "{b:02x}");
    }
    s
}

/// Lowercase SHA-256 hex.
pub fn sha256_hex(data: &[u8]) -> String {
    hex(digest::digest(&digest::SHA256, data).as_ref())
}

/// An incremental SHA-256.
pub struct Sha256(digest::Context);

impl Sha256 {
    pub fn new() -> Self {
        Self(digest::Context::new(&digest::SHA256))
    }

    pub fn update(&mut self, data: &[u8]) {
        self.0.update(data);
    }

    pub fn finish_hex(self) -> String {
        hex(self.0.finish().as_ref())
    }
}

impl Default for Sha256 {
    fn default() -> Self {
        Self::new()
    }
}

pub fn sha256_file(path: &Path) -> Result<String> {
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; 1 << 16];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finish_hex())
}

/// Constant-time comparison of two hex digests, case-insensitive. Malformed hex never matches.
pub fn hashes_equal(a: &str, b: &str) -> bool {
    fn decode(s: &str) -> Option<Vec<u8>> {
        if !s.len().is_multiple_of(2) {
            return None;
        }
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(s.get(i..i + 2)?, 16).ok())
            .collect()
    }
    match (decode(a), decode(b)) {
        (Some(x), Some(y)) if x.len() == y.len() => x.iter().zip(&y).fold(0u8, |acc, (p, q)| acc | (p ^ q)) == 0,
        _ => false,
    }
}

/// `Err(HashMismatch)` unless `actual` matches `expected`.
pub fn expect_hash(key: &str, expected: &str, actual: &str) -> Result<()> {
    if hashes_equal(expected, actual) {
        Ok(())
    } else {
        Err(Error::HashMismatch { key: key.into(), expected: expected.into(), actual: actual.into() })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn canonical_form_sorts_keys_strips_space_and_escapes_like_the_dotnet_signer() {
        let json = r#"{ "b": [1, {"z": null, "a": true}], "a": "x+y<&>\u0001\n é😀", "signature": {"sig": "s"} }"#;
        let got = String::from_utf8(canonicalize_for_signing(json).unwrap()).unwrap();
        assert_eq!(got, "{\"a\":\"x+y<&>\\u0001\\n\\u2028\u{e9}\\uD83D\\uDE00\",\"b\":[1,{\"a\":true,\"z\":null}]}");
    }

    #[test]
    fn a_signature_verifies_only_for_the_signed_bytes_and_the_right_key() {
        let seed = [7u8; 32];
        let mut keys = KeyMap::new();
        keys.insert("k1".into(), public_key_from_seed(&seed).unwrap());
        let doc = r#"{"schemaVersion":1,"name":"x"}"#;
        let envelope = sign(doc, "k1", &seed).unwrap();
        assert!(verify(doc, Some(&envelope), &keys));
        // Whitespace and order do not matter; content does.
        assert!(verify(r#"{ "name": "x", "schemaVersion": 1 }"#, Some(&envelope), &keys));
        assert!(!verify(r#"{"schemaVersion":1,"name":"y"}"#, Some(&envelope), &keys));
        assert!(!verify(r#"{"schemaVersion":1,"name":"x","extra":0}"#, Some(&envelope), &keys));
        let unknown = SignatureEnvelope { key_id: "k2".into(), ..envelope.clone() };
        assert!(!verify(doc, Some(&unknown), &keys));
        assert!(!verify(doc, None, &keys));
    }

    #[test]
    fn hash_comparison_ignores_case_and_rejects_malformed_hex() {
        assert!(hashes_equal("ABcd", "abCD"));
        assert!(!hashes_equal("abcd", "abce"));
        assert!(!hashes_equal("abc", "abc"));
        assert!(!hashes_equal("zz", "zz"));
        assert_eq!(
            sha256_hex(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }
}
