// CDN key scheme, mirroring CaligoExpress.Publishing.CdnKeys so the worker can compute exactly which
// artifacts a release references (for replication and garbage collection).
//
// Multi-app layout: the DEFAULT app keeps the historical ROOT layout (config.json, <channel>/release.json,
// chunks/..., bootstrapper/..., macapp/...) because shipped bootstrappers hard-code those paths. Every
// other app gets the same scheme under an `apps/<app>/` prefix, so all apps can share hosts and buckets.

import type { BootstrapperManifest, PackageManifest, ReleaseManifest } from "./types.ts";

export const APPS_ROOT = "apps/";

/** Storage-key prefix for an app: "" for the default (legacy root layout), `apps/<app>/` otherwise. */
export const appPrefix = (app: string, defaultApp: string): string =>
  app === defaultApp ? "" : `${APPS_ROOT}${app}/`;

/** The key scheme rooted at a prefix ("" = legacy root layout). */
export function cdnKeysFor(prefix: string) {
  return {
    config: `${prefix}config.json`,
    release: (channel: string) => `${prefix}${channel}/release.json`,
    bootstrapperManifest: (channel: string) => `${prefix}${channel}/bootstrapper.json`,
    packageManifest: (name: string, version: string) => `${prefix}${name}/${version}/package.json`,
    // Content-addressed chunk object, sharded by the first hash byte (matches CaligoExpress.Publishing.CdnKeys.Chunk).
    chunk: (hash: string) => `${prefix}chunks/${hash.slice(0, 2)}/${hash}`,
    bootstrapper: (version: string) => `${prefix}bootstrapper/${version}/bootstrapper.bin`,
  } as const;
}

/** Legacy-root scheme (the default app's layout). */
export const CdnKeys = cdnKeysFor("");

/** Which app a storage key belongs to: `apps/<app>/...` → that app, anything else → the default app. */
export function keyOwnerApp(key: string, defaultApp: string): string {
  if (!key.startsWith(APPS_ROOT)) return defaultApp;
  const rest = key.slice(APPS_ROOT.length);
  const slash = rest.indexOf("/");
  return slash > 0 ? rest.slice(0, slash) : defaultApp; // a bare "apps/x" file is not an app namespace
}

/** True when the key lies inside the given app's namespace (and no other app's). */
export function keyBelongsToApp(key: string, app: string, defaultApp: string): boolean {
  return keyOwnerApp(key, defaultApp) === app;
}

/** Chunk test on an app-local (prefix-stripped) key. */
export function isChunk(localKey: string): boolean {
  return localKey.startsWith("chunks/");
}

/**
 * The base keys an app release points at: each package's package.json. Chunk keys are NOT here — they
 * live inside the package.json, which the GC fetches and expands via {@link chunkKeysOf}. The bootstrapper
 * is a separate track ({@link bootstrapperKeysOf}). Used to seed the keep set during garbage collection.
 */
export function referencedKeys(release: ReleaseManifest): Set<string> {
  const keys = new Set<string>();
  for (const p of release.packages) keys.add(p.manifest.key);
  if (release.bootstrapper) keys.add(release.bootstrapper.full.key); // keep the legacy shim's binary
  if (release.macApp) keys.add(release.macApp.artifact.key); // keep the macOS whole-app bundle zip
  if (release.linuxApp) keys.add(release.linuxApp.artifact.key); // keep the Linux AppImage
  return keys;
}

/** The binaries a bootstrapper manifest points at — the Windows `full` and, if present, the macOS `macFull`. */
export function bootstrapperKeysOf(boot: BootstrapperManifest): string[] {
  const keys = [boot.full.key];
  if (boot.macFull) keys.push(boot.macFull.key);
  return keys;
}

/** Every chunk object a package manifest references (its top-level chunk list), keyed under `prefix`.
 *  Tolerates an older package.json that predates the top-level chunk list (returns none rather than throwing). */
export function chunkKeysOf(manifest: PackageManifest, prefix = ""): string[] {
  const keys = cdnKeysFor(prefix);
  return (manifest.chunks ?? []).map((c) => keys.chunk(c.hash));
}
