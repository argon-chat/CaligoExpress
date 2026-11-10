export interface Me {
  sub?: string;
  name?: string;
  email?: string;
  preferredUsername?: string;
  roles?: string[];
  scope?: string;
}

export interface OverviewRelease {
  releaseId: string;
  releaseSeq: number;
  createdAt: string;
  packages: { name: string; version: string }[];
  /** macOS whole-app payload "bundle@version" (null on Windows releases). */
  mac?: string | null;
}

export interface OverviewChannel {
  channel: string;
  currentSeq: number;
  /** Current bootstrapper version from the separate bootstrapper track (null if none). */
  bootstrapper: string | null;
  release: OverviewRelease | null;
}

export interface ReleaseListItem {
  seq: number;
  releaseId: string;
  createdAt: string;
  publishedAt: string;
  current: boolean;
  packages: string[];
  /** macOS whole-app payload "bundle@version" (null on Windows releases). */
  mac?: string | null;
}

export interface ReleasesResponse {
  channel: string;
  current: number | null;
  releases: ReleaseListItem[];
}

// ── bootstrapper track (its own seq history + pointer) ──
export interface BootstrapperListItem {
  seq: number;
  version: string;
  createdAt: string;
  publishedAt: string;
  current: boolean;
  /** Which platform binaries this bootstrapper carries (Windows `full` / macOS `macFull`). */
  win?: boolean;
  mac?: boolean;
}

export interface BootstrappersResponse {
  channel: string;
  current: number | null;
  bootstrappers: BootstrapperListItem[];
}

// ── full release manifest (GET /admin/release/:channel/:seq) for the detail view ──
export interface ArtifactRef {
  key: string;
  hash: string;
  size: number;
}
export interface FullPackageRef {
  name: string;
  version: string;
  required: boolean;
  deps: string[];
  manifest: ArtifactRef;
}
export interface MonolithRef {
  artifact: ArtifactRef;
  bundleName: string;
  version: string;
}
export interface ReleaseManifestFull {
  schemaVersion: number;
  channel: string;
  releaseSeq: number;
  releaseId: string;
  createdAt: string;
  packages: FullPackageRef[];
  launch?: { bin: string; args: string[]; workingDir?: string };
  signature?: { keyId: string; alg: string; sig: string };
  /** macOS whole-app payload + its launch spec (present on mac releases). */
  macApp?: MonolithRef;
  macLaunch?: { bin: string; args: string[]; workingDir?: string };
  /** Linux whole-app payload, one self-replacing AppImage (present on Linux releases). */
  linuxApp?: MonolithRef;
}

// ── live config.json served to clients (GET /config.json) ──
export interface LiveConfig {
  schemaVersion: number;
  mirrors: { host: string; regions?: string[]; priority?: number; scheme?: string }[];
  channels: Record<string, string>;
  publicKeys: { id: string; alg: string; key: string }[];
  minBootstrapper: string;
  signature?: { keyId: string; alg: string; sig: string };
}

/** A mirror as returned by the worker for display: accessKeyId masked, secret presence only. */
export interface MirrorRedacted {
  name: string;
  endpoint: string;
  bucket: string;
  region: string;
  publicBaseUrl?: string;
  accessKeyId: string; // masked, e.g. ••••1234
  hasSecret: boolean;
}

export interface AdminSettings {
  /** The app these settings were read for (pinned keys are per app; mirrors/retention are global). */
  app?: string;
  manifestMirrors: MirrorRedacted[];
  contentMirrors: MirrorRedacted[];
  keepPerChannel: number;
  /** When true the GC cron only reports what it would delete (rollout safety switch). */
  gcDryRun?: boolean;
  /** Pinned public keys are not secrets — full id -> base64 map. */
  pinnedKeys: Record<string, string>;
}

/** Editable mirror row in the UI. Credentials start blank; leaving them blank keeps the stored value. */
export interface MirrorRow {
  name: string;
  endpoint: string;
  bucket: string;
  region: string;
  publicBaseUrl: string;
  accessKeyId: string;
  secretAccessKey: string;
  hasSecret: boolean;
  accessKeyHint: string; // masked existing accessKeyId, shown as placeholder
}

/** The payload sent on save (credentials omitted when blank). */
export interface MirrorConfigInput {
  name: string;
  endpoint: string;
  bucket: string;
  region: string;
  publicBaseUrl?: string;
  accessKeyId: string;
  secretAccessKey: string;
}
