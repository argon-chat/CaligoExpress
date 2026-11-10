//! A stored chunk object: one codec tag byte, then the payload (0 raw, 1 gzip). A chunk is addressed
//! by the SHA-256 of its plaintext, so the codec never affects dedup or reassembly.

use std::io::{Read, Write};

use flate2::Compression;
use flate2::read::GzDecoder;
use flate2::write::GzEncoder;

use crate::{Error, Result};

const RAW: u8 = 0;
const GZIP: u8 = 1;

pub fn decode(stored: &[u8]) -> Result<Vec<u8>> {
    match stored.split_first() {
        None => Ok(Vec::new()),
        Some((&RAW, payload)) => Ok(payload.to_vec()),
        Some((&GZIP, payload)) => {
            let mut plain = Vec::new();
            GzDecoder::new(payload).read_to_end(&mut plain)?;
            Ok(plain)
        }
        Some((tag, _)) => Err(Error::other(format!("unknown chunk codec tag {tag}"))),
    }
}

/// Compressed when that is actually smaller, else raw: never more than one byte over the plaintext.
pub fn encode(plain: &[u8]) -> Vec<u8> {
    let mut gz = GzEncoder::new(vec![GZIP], Compression::best());
    gz.write_all(plain).expect("writing to a Vec cannot fail");
    let compressed = gz.finish().expect("writing to a Vec cannot fail");
    if compressed.len() < plain.len() + 1 {
        return compressed;
    }
    let mut raw = Vec::with_capacity(plain.len() + 1);
    raw.push(RAW);
    raw.extend_from_slice(plain);
    raw
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn both_codecs_round_trip() {
        let text = b"hello hello hello hello hello hello hello hello".repeat(20);
        let packed = encode(&text);
        assert_eq!(packed[0], GZIP);
        assert_eq!(decode(&packed).unwrap(), text);
        let noise: Vec<u8> = (0..64u32).map(|i| (i.wrapping_mul(2_654_435_761) >> 13) as u8).collect();
        let raw = encode(&noise);
        assert_eq!(raw[0], RAW);
        assert_eq!(decode(&raw).unwrap(), noise);
        assert!(decode(&[9, 1]).is_err());
    }
}
