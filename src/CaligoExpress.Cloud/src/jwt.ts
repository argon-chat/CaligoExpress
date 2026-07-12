// Minimal ES256 (ECDSA P-256) JWT verification against the Aegis SSO JWKS, using WebCrypto only.
// Aegis signs id/access tokens with ES256; a JWT's ES256 signature is the raw r||s pair WebCrypto
// expects, so no DER conversion is needed. JWKS is cached briefly.

export interface JwtClaims {
  iss?: string;
  sub?: string;
  aud?: string | string[];
  exp?: number;
  iat?: number;
  scope?: string;
  scopes?: string[];
  roles?: string[];
  email?: string;
  name?: string;
  preferred_username?: string;
  operator_id?: string;
  [key: string]: unknown;
}

interface Jwk {
  kid?: string;
  kty: string;
  crv?: string;
  x?: string;
  y?: string;
  alg?: string;
}

const JWKS_TTL_MS = 10 * 60 * 1000;
let jwksCache: { uri: string; keys: Jwk[]; fetchedAt: number } | null = null;

async function getJwks(uri: string): Promise<Jwk[]> {
  if (jwksCache && jwksCache.uri === uri && Date.now() - jwksCache.fetchedAt < JWKS_TTL_MS) {
    return jwksCache.keys;
  }
  const res = await fetch(uri);
  if (!res.ok) throw new Error(`JWKS fetch failed: ${res.status}`);
  const body = (await res.json()) as { keys: Jwk[] };
  jwksCache = { uri, keys: body.keys ?? [], fetchedAt: Date.now() };
  return jwksCache.keys;
}

function b64urlToBytes(s: string): Uint8Array {
  const b64 = s.replaceAll("-", "+").replaceAll("_", "/").padEnd(Math.ceil(s.length / 4) * 4, "=");
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function b64urlToString(s: string): string {
  return new TextDecoder().decode(b64urlToBytes(s));
}

export interface VerifyOptions {
  issuer: string;
  jwksUri: string;
  /** Optional clock skew tolerance in seconds. */
  leewaySec?: number;
}

export async function verifyJwt(token: string, opts: VerifyOptions): Promise<JwtClaims> {
  const parts = token.split(".");
  if (parts.length !== 3) throw new Error("malformed token");
  const [h, p, s] = parts as [string, string, string];

  const header = JSON.parse(b64urlToString(h)) as { alg: string; kid?: string };
  if (header.alg !== "ES256") throw new Error(`unsupported alg ${header.alg}`);

  const claims = JSON.parse(b64urlToString(p)) as JwtClaims;
  const now = Math.floor(Date.now() / 1000);
  const leeway = opts.leewaySec ?? 60;
  if (claims.exp !== undefined && now > claims.exp + leeway) throw new Error("token expired");
  if (claims.iss !== opts.issuer) throw new Error("issuer mismatch");

  const jwks = await getJwks(opts.jwksUri);
  const jwk = jwks.find((k) => k.kid === header.kid) ?? jwks.find((k) => k.kty === "EC");
  if (!jwk || jwk.kty !== "EC") throw new Error("no matching EC signing key");

  const key = await crypto.subtle.importKey(
    "jwk",
    { kty: "EC", crv: jwk.crv ?? "P-256", x: jwk.x, y: jwk.y, ext: true },
    { name: "ECDSA", namedCurve: "P-256" },
    false,
    ["verify"],
  );

  const ok = await crypto.subtle.verify(
    { name: "ECDSA", hash: "SHA-256" },
    key,
    b64urlToBytes(s),
    new TextEncoder().encode(`${h}.${p}`),
  );
  if (!ok) throw new Error("signature verification failed");

  return claims;
}

/** True if the token carries the given scope (space-delimited `scope`, `scopes[]`, or `roles[]`). */
export function hasScope(claims: JwtClaims, scope: string): boolean {
  if (typeof claims.scope === "string" && claims.scope.split(" ").includes(scope)) return true;
  if (Array.isArray(claims.scopes) && claims.scopes.includes(scope)) return true;
  if (Array.isArray(claims.roles) && claims.roles.includes(scope)) return true;
  return false;
}
