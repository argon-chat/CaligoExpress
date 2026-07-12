// TypeScript mirror of the CaligoExpress C# manifest DTOs (camelCase wire format).
// Keep these in sync with src/CaligoExpress/Manifests/*.cs.

export interface ArtifactRef {
  key: string;
  hash: string;
  size: number;
}

export interface SignatureEnvelope {
  keyId: string;
  alg: string;
  sig: string;
}

export interface PackageRef {
  name: string;
  version: string;
  required: boolean;
  deps: string[];
  /** The package.json listing (carries each file's content-defined chunk list). No full zip / deltas. */
  manifest: ArtifactRef;
}

/** The bootstrapper's own signed pointer doc, decoupled from app releases (its own seq + binary).
 *  `full` is the Windows .exe; `macFull` (when present) is the macOS .app ditto-zip — a different binary,
 *  so each OS downloads its own. */
export interface BootstrapperManifest {
  schemaVersion: number;
  /** App slug this manifest belongs to. Absent on legacy (default-app) manifests — the field is part
   *  of the signed payload, so the worker can reject a manifest published to the wrong app. */
  app?: string;
  channel: string;
  seq: number;
  version: string;
  createdAt: string;
  full: ArtifactRef;
  signature?: SignatureEnvelope;
  macFull?: ArtifactRef;
}

// ── package.json shape (only what the worker's GC needs to expand chunk references) ──

export interface ChunkEntry {
  hash: string;
  size: number;
}

export interface RegionEntry {
  chunk: string;
  offset: number;
  length: number;
}

export interface FileEntry {
  path: string;
  hash: string;
  size: number;
  regions: RegionEntry[];
  exec?: boolean;
}

export interface PackageManifest {
  schemaVersion: number;
  name: string;
  version: string;
  targetRoot: string;
  /** Every unique chunk object the package references — the GC keep-set source. */
  chunks: ChunkEntry[];
  files: FileEntry[];
  hooks: unknown[];
}

export interface LaunchSpec {
  bin: string;
  args: string[];
  workingDir?: string;
}

/** macOS whole-app delivery: one content-addressed bundle zip the client downloads + atomically swaps
 *  (signed/notarized .app can't be chunk-reassembled). Mirrors CaligoExpress.Manifests.MonolithRef. */
export interface MonolithRef {
  artifact: ArtifactRef;
  bundleName: string;
  version: string;
}

export interface ReleaseManifest {
  schemaVersion: number;
  /** App slug this manifest belongs to. Absent on legacy (default-app) manifests. */
  app?: string;
  channel: string;
  releaseSeq: number;
  releaseId: string;
  createdAt: string;
  packages: PackageRef[];
  launch?: LaunchSpec;
  /** Legacy compat shim for pre-decoupling clients; GC keeps its binary so a mid-migration client can still self-update. */
  bootstrapper?: { version: string; full: ArtifactRef };
  signature?: SignatureEnvelope;
  /** macOS whole-app payload + its launch spec. Null on Windows releases; both platforms can share one
   *  signed release.json (Windows packages + mac bundle) published from separate CI jobs. */
  macApp?: MonolithRef;
  macLaunch?: LaunchSpec;
}

export interface MirrorEntry {
  host: string;
  regions?: string[];
  priority?: number;
  scheme?: string;
}

export interface PublicKeyEntry {
  id: string;
  alg: string;
  key: string;
}

export interface ConfigManifest {
  schemaVersion: number;
  mirrors: MirrorEntry[];
  channels: Record<string, string>;
  publicKeys: PublicKeyEntry[];
  minBootstrapper: string;
  signature?: SignatureEnvelope;
}

/** The runtime environment, identical between Cloudflare (real D1) and local Bun (sqlite shim). */
export interface Env {
  DB: D1Like;
  /** Bearer token guarding mutating endpoints. Full access across ALL apps (see also per-app tokens
   *  stored in settings, which are scoped to one app's routes). */
  PUBLISH_TOKEN: string;
  /** The app served on the LEGACY (un-prefixed) routes and storage layout. Default "argon". Shipped
   *  Argon bootstrappers hard-code the root config.json/chunks layout, so this app's keys stay at the
   *  bucket root forever; every other app lives under apps/<app>/. */
  DEFAULT_APP?: string;
  /** Optional JSON map {keyId: base64PublicKey} for defense-in-depth release signature checks. */
  PINNED_PUBKEYS?: string;
  /** JSON array of content-mirror configs — bootstrap default for content S3; admins override in D1. */
  MIRRORS?: string;
  /** Alias of MIRRORS for clarity (content mirrors). */
  CONTENT_MIRRORS?: string;
  /** JSON array of manifest-mirror configs (S3 behind the *-manifest hosts). */
  MANIFEST_MIRRORS?: string;
  /** Releases retained per channel for rollback; older ones are GC-eligible. Default 5. */
  KEEP_PER_CHANNEL?: string;

  // ── SSO (Aegis) for the admin UI ──
  /** OIDC issuer for admin SSO (optional). */
  AEGIS_ISSUER?: string;
  /** JWKS endpoint for admin SSO (optional). */
  AEGIS_JWKS?: string;
  /** Scope required to use the admin API; default infrastructure.write */
  ADMIN_SCOPE?: string;
  /** Allowed CORS origin for the admin SPA. */
  ADMIN_ORIGIN?: string;
}

/** Hono per-request variables. */
export interface Vars {
  user: import("./jwt.ts").JwtClaims;
}

/** The app the legacy (un-prefixed) routes and root storage layout belong to. */
export const defaultAppOf = (env: Env): string => env.DEFAULT_APP?.trim() || "argon";

/** App slugs are storage-key and URL segments — keep them boring. */
export const isValidApp = (s: string): boolean => /^[a-z0-9][a-z0-9-]{0,63}$/.test(s);

// SSO is optional: with no issuer/JWKS configured, JWT verification simply fails and machine tokens
// (PUBLISH_TOKEN / per-app tokens) remain the only way in. Set the AEGIS_* vars to enable operator SSO.
export const AegisDefaults = {
  issuer: "",
  jwks: "",
  adminScope: "infrastructure.write",
  adminOrigin: "http://localhost:5173",
} as const;

// ── Minimal D1 surface, satisfied by Cloudflare's D1Database and the bun:sqlite shim. ──

export interface D1Like {
  prepare(sql: string): D1StmtLike;
}

export interface D1StmtLike {
  bind(...args: unknown[]): D1StmtLike;
  all<T = Record<string, unknown>>(): Promise<{ results: T[] }>;
  first<T = Record<string, unknown>>(): Promise<T | null>;
  run(): Promise<unknown>;
}
