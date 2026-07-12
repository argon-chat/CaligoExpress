// Runtime configuration resolution. The admin UI writes mirror configs / secrets into D1; these helpers
// prefer the D1 value and fall back to the env-var bootstrap default. This is what lets an operator
// bootstrap and rotate the worker's mirrors, retention and pinned keys entirely from the admin.
//
// There are TWO mirror classes:
//   - manifest mirrors: S3 behind the *-manifest hosts; hold config.json + <channel>/release.json.
//   - content mirrors:  S3 behind the *-updates hosts; hold the artifacts (zips/deltas/bootstrapper).
// The worker replicates manifests to the manifest mirrors and prunes stale artifacts on the content ones.

import * as db from "./db.ts";
import { S3Mirror, parseMirrors, type MirrorConfig } from "./s3.ts";
import type { Env } from "./types.ts";

export const SettingsKeys = {
  manifestMirrors: "manifestMirrors",
  contentMirrors: "contentMirrors",
  keepPerChannel: "keepPerChannel",
  pinnedKeys: "pinnedKeys",
  /** JSON map {app: token} — per-app publish tokens, each valid only on its app's routes. */
  appTokens: "appTokens",
  /** "1" → the GC cron reports what it WOULD delete without deleting (staged-rollout safety switch). */
  gcDryRun: "gcDryRun",
} as const;

/** Settings key holding an app's pinned public keys. The default app keeps the historical un-suffixed
 *  key (and the PINNED_PUBKEYS env fallback), so existing deployments migrate with zero data changes. */
export const pinnedKeysKeyFor = (app: string, defaultApp: string): string =>
  app === defaultApp ? SettingsKeys.pinnedKeys : `${SettingsKeys.pinnedKeys}:${app}`;

async function readMirrors(env: Env, key: string, envFallback?: string): Promise<MirrorConfig[]> {
  const fromDb = await db.getSetting(env.DB, key);
  if (fromDb) {
    try {
      const arr = JSON.parse(fromDb);
      if (Array.isArray(arr)) return arr as MirrorConfig[];
    } catch {
      /* fall through */
    }
  }
  return parseMirrors(envFallback);
}

export const getManifestMirrorConfigs = (env: Env) =>
  readMirrors(env, SettingsKeys.manifestMirrors, env.MANIFEST_MIRRORS);

export const getContentMirrorConfigs = (env: Env) =>
  readMirrors(env, SettingsKeys.contentMirrors, env.CONTENT_MIRRORS ?? env.MIRRORS);

export const manifestMirrorsFor = async (env: Env): Promise<S3Mirror[]> =>
  (await getManifestMirrorConfigs(env)).map((c) => new S3Mirror(c));

export const contentMirrorsFor = async (env: Env): Promise<S3Mirror[]> =>
  (await getContentMirrorConfigs(env)).map((c) => new S3Mirror(c));

export async function setMirrorConfigs(env: Env, key: string, configs: MirrorConfig[]): Promise<void> {
  await db.setSetting(env.DB, key, JSON.stringify(configs));
}

export interface PresignTarget {
  mirror: string;
  url: string;
}
export interface PresignItem {
  key: string;
  targets: PresignTarget[];
}

/**
 * Presigned PUT URLs for the given keys across the chosen mirror set. This is how publishers upload
 * artifacts without ever holding S3 credentials: the worker signs short-lived URLs, the client PUTs the
 * bytes directly to each mirror.
 *
 * With `skipExisting` (used for content-addressed chunk uploads), each mirror is listed once and a key
 * already present there is omitted from that mirror's targets — so a publish only uploads the chunks a
 * mirror is missing. A key already on every mirror comes back with an empty `targets` array.
 */
export async function presignUploads(
  env: Env, kind: "content" | "manifest", keys: string[], expiresIn: number, skipExisting = false,
): Promise<PresignItem[]> {
  const configs = kind === "manifest" ? await getManifestMirrorConfigs(env) : await getContentMirrorConfigs(env);
  const mirrors = configs.map((c) => new S3Mirror(c));

  const present = new Map<string, Set<string>>();
  if (skipExisting) {
    for (const m of mirrors) {
      try {
        present.set(m.cfg.name, new Set(await m.list()));
      } catch {
        present.set(m.cfg.name, new Set()); // on list failure, assume nothing present → upload it
      }
    }
  }

  const items: PresignItem[] = [];
  for (const key of keys) {
    const targets: PresignTarget[] = [];
    for (const m of mirrors) {
      if (skipExisting && present.get(m.cfg.name)?.has(key)) continue; // already on this mirror
      targets.push({ mirror: m.cfg.name, url: await m.presignPut(key, expiresIn) });
    }
    items.push({ key, targets });
  }
  return items;
}

/**
 * Preserves stored credentials when an edit leaves them blank: an incoming mirror whose accessKeyId /
 * secretAccessKey is empty keeps the value already stored for the mirror of the same name. This lets the
 * admin edit non-secret fields without re-typing secrets (the UI shows them redacted).
 */
export function mergeMirrorSecrets(incoming: MirrorConfig[], existing: MirrorConfig[]): MirrorConfig[] {
  const byName = new Map(existing.map((m) => [m.name, m]));
  return incoming.map((m) => {
    const prev = byName.get(m.name);
    return {
      ...m,
      accessKeyId: m.accessKeyId?.trim() ? m.accessKeyId : prev?.accessKeyId ?? "",
      secretAccessKey: m.secretAccessKey?.trim() ? m.secretAccessKey : prev?.secretAccessKey ?? "",
    };
  });
}

export async function getKeepPerChannel(env: Env): Promise<number> {
  const fromDb = await db.getSetting(env.DB, SettingsKeys.keepPerChannel);
  const n = Number(fromDb ?? env.KEEP_PER_CHANNEL ?? "5");
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 5;
}

export async function getPinnedKeys(env: Env, app: string, defaultApp: string): Promise<Record<string, string>> {
  const stored = await db.getSetting(env.DB, pinnedKeysKeyFor(app, defaultApp));
  const raw = stored ?? (app === defaultApp ? env.PINNED_PUBKEYS : undefined);
  if (!raw) return {};
  try {
    return JSON.parse(raw) as Record<string, string>;
  } catch {
    return {};
  }
}

/** Per-app publish tokens: {app: token}. The master PUBLISH_TOKEN is separate and spans all apps. */
export async function getAppTokens(env: Env): Promise<Record<string, string>> {
  const raw = await db.getSetting(env.DB, SettingsKeys.appTokens);
  if (!raw) return {};
  try {
    return JSON.parse(raw) as Record<string, string>;
  } catch {
    return {};
  }
}

/** Sets or (with an empty token) removes an app's publish token. */
export async function setAppToken(env: Env, app: string, token: string): Promise<void> {
  const tokens = await getAppTokens(env);
  if (token) tokens[app] = token;
  else delete tokens[app];
  await db.setSetting(env.DB, SettingsKeys.appTokens, JSON.stringify(tokens));
}

export async function getGcDryRun(env: Env): Promise<boolean> {
  return (await db.getSetting(env.DB, SettingsKeys.gcDryRun)) === "1";
}

/** Mirror configs with credentials redacted, for display in the admin UI. */
export function redactMirrors(configs: MirrorConfig[]) {
  return configs.map((c) => ({
    name: c.name,
    endpoint: c.endpoint,
    bucket: c.bucket,
    region: c.region,
    publicBaseUrl: c.publicBaseUrl ?? "",
    accessKeyId: c.accessKeyId ? maskTail(c.accessKeyId) : "",
    hasSecret: !!c.secretAccessKey,
  }));
}

function maskTail(s: string): string {
  return s.length <= 4 ? "••••" : `••••${s.slice(-4)}`;
}
