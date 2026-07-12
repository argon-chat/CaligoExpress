// Canonical JSON + Ed25519 verification, byte-compatible with the C# side
// (CaligoExpress.Crypto.JsonCanonicalizer / ManifestSigning).
//
// Canonical form: object keys sorted, compact, the top-level "signature" stripped. All our numbers
// are integers and all signed string values are ASCII, so JSON.stringify matches the C# Utf8JsonWriter
// output exactly — which is what lets a release signed offline in .NET verify here unchanged.

export function canonicalize(value: unknown): string {
  if (value === null || typeof value !== "object") {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return "[" + value.map(canonicalize).join(",") + "]";
  }
  const obj = value as Record<string, unknown>;
  const keys = Object.keys(obj)
    .filter((k) => obj[k] !== undefined)
    .sort(); // default JS string sort == ordinal for the ASCII keys we use
  return "{" + keys.map((k) => JSON.stringify(k) + ":" + canonicalize(obj[k])).join(",") + "}";
}

export function canonicalBytesForSigning(manifest: object): Uint8Array {
  const { signature: _omit, ...rest } = manifest as Record<string, unknown>;
  return new TextEncoder().encode(canonicalize(rest));
}

export function base64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export async function verifyEd25519(
  message: Uint8Array,
  sigBase64: string,
  publicKeyBase64: string,
): Promise<boolean> {
  try {
    const key = await crypto.subtle.importKey(
      "raw",
      base64ToBytes(publicKeyBase64),
      { name: "Ed25519" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify({ name: "Ed25519" }, key, base64ToBytes(sigBase64), message);
  } catch {
    return false;
  }
}

/** Verifies a signed manifest against a pinned-key map {keyId: base64PublicKey}. */
export async function verifySignedManifest(
  manifest: { signature?: { keyId: string; alg: string; sig: string } },
  keys: Record<string, string>,
): Promise<boolean> {
  const sig = manifest.signature;
  if (!sig || sig.alg !== "ed25519") return false;
  const pub = keys[sig.keyId];
  if (!pub) return false;
  return verifyEd25519(canonicalBytesForSigning(manifest), sig.sig, pub);
}
