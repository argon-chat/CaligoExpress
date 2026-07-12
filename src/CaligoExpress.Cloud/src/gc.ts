// Garbage collection + mirror self-heal, run on a Cloudflare cron trigger (and exposed as POST /gc).
//
// Multi-app: the keep set is computed PER APP (each app's channels, releases and bootstrappers), and
// every listed bucket key is attributed to exactly one app by its prefix (`apps/<app>/...` → that app,
// anything else → the default app). A key whose prefix names an app this worker doesn't know is NEVER
// deleted (it may belong to an app whose first publish is still in flight) — it is only counted.
//
// For each channel it keeps the current release plus the most recent KEEP_PER_CHANNEL-1 prior ones
// (so rollback stays possible) and everything they reference. It then (1) re-uploads the current
// signed manifests to every manifest mirror — fixing drift if a mirror missed a write — and (2) deletes
// artifacts no kept release references any more (superseded package.json files, old bootstrappers, and
// chunk objects no kept release uses). It never touches release.json / config.json.
//
// Chunk references live inside each package.json, not the release manifest, so the keep set is expanded
// by fetching every kept package.json. If any of those fetches fail we keep all of THAT app's chunks
// (fail safe) — deleting a chunk a live release still needs would corrupt installs.
//
// Dry-run (POST /gc {dryRun:true}, or the `gcDryRun` setting for the cron) reports what would be
// deleted without deleting — the safety switch for staged rollouts of layout changes.

import * as db from "./db.ts";
import { appPrefix, bootstrapperKeysOf, cdnKeysFor, chunkKeysOf, isChunk, keyOwnerApp, referencedKeys } from "./keys.ts";
import { contentMirrorsFor, getGcDryRun, getKeepPerChannel, manifestMirrorsFor } from "./settings.ts";
import type { S3Mirror } from "./s3.ts";
import { defaultAppOf, type BootstrapperManifest, type Env, type PackageManifest, type ReleaseManifest } from "./types.ts";

export interface GcReport {
  /** Apps swept this run (default app always included). */
  apps: string[];
  channels: number;
  keptReleases: number;
  keptBootstrappers: number;
  referencedKeys: number;
  keepPerChannel: number;
  replicated: number;
  /** Total object keys the content mirrors reported (0 here means list() returned nothing — the real problem). */
  listed: number;
  /** Keys that were prunable + unreferenced but skipped because chunk expansion was incomplete (fail-safe). */
  skippedChunks: number;
  /** Keys under apps/<x>/ where <x> is not an app this worker knows — never touched, only counted. */
  unknownAppKeys: number;
  deleted: number;
  /** True when nothing was actually deleted from mirrors or D1 — the report shows what WOULD happen. */
  dryRun: boolean;
  /** In dry-run: how many keys would be deleted, and a sample (up to 100) of them. */
  wouldDeleteTotal: number;
  wouldDelete: string[];
  /** Old release/bootstrapper rows pruned from D1 (beyond the keep window, never the live pointer). */
  dbDeleted: number;
  errors: string[];
}

const asMessage = (e: unknown): string => (e instanceof Error ? e.message : String(e));

interface AppKeepState {
  prefix: string;
  referenced: Set<string>;
  chunksExpanded: boolean; // false → never prune this app's chunks this run
}

export async function runGc(env: Env, opts: { dryRun?: boolean } = {}): Promise<GcReport> {
  const dryRun = opts.dryRun ?? (await getGcDryRun(env));
  const defApp = defaultAppOf(env);
  const keep = await getKeepPerChannel(env);
  const manifestMirrors = await manifestMirrorsFor(env);
  const contentMirrors = await contentMirrorsFor(env);
  const errors: string[] = [];

  const apps = new Set(await db.listApps(env.DB));
  apps.add(defApp);

  // 1) Build each app's keep set: config + each channel's release pointer + the kept releases' base keys,
  //    then expand chunk references out of every kept package.json (fetched from a content mirror).
  const perApp = new Map<string, AppKeepState>();
  let channelCount = 0;
  let keptReleases = 0;
  let keptBootstrappers = 0;
  let dbDeleted = 0;
  const pkgManifestCache = new Map<string, PackageManifest | null>();

  for (const app of apps) {
    const prefix = appPrefix(app, defApp);
    const keys = cdnKeysFor(prefix);
    const state: AppKeepState = { prefix, referenced: new Set([keys.config]), chunksExpanded: true };
    perApp.set(app, state);

    const channels = await db.allChannels(env.DB, app);
    channelCount += channels.length;

    for (const ch of channels) {
      state.referenced.add(keys.release(ch.channel));
      const allSeqs = await db.listReleaseSeqs(env.DB, app, ch.channel);
      const pointer = await db.getPointer(env.DB, app, ch.channel);
      const keptSeqs = new Set<number>(allSeqs.slice(0, keep));
      if (pointer != null) keptSeqs.add(pointer); // never drop the live release, even if older than the window
      for (const seq of keptSeqs) {
        try {
          const row = await db.getRelease(env.DB, app, ch.channel, seq);
          if (!row) continue;
          keptReleases++;
          const manifest = JSON.parse(row.manifest_json) as ReleaseManifest;
          for (const k of referencedKeys(manifest)) state.referenced.add(k);

          for (const p of manifest.packages) {
            const pj = await fetchPackageManifest(contentMirrors, p.manifest.key, pkgManifestCache);
            if (pj) {
              for (const k of chunkKeysOf(pj, prefix)) state.referenced.add(k);
            } else {
              state.chunksExpanded = false;
              errors.push(`could not read ${p.manifest.key} to expand chunks`);
            }
          }
        } catch (e) {
          state.chunksExpanded = false; // can't fully account for this release -> never prune this app's chunks
          errors.push(`keep ${app}/${ch.channel}#${seq}: ${asMessage(e)}`);
        }
      }
      // Drop release rows older than the keep window from D1 (their artifacts are pruned below / next run),
      // so the admin only ever lists releases you can actually roll back to.
      for (const seq of allSeqs) {
        if (!keptSeqs.has(seq)) {
          if (!dryRun) await db.deleteRelease(env.DB, app, ch.channel, seq);
          dbDeleted++;
        }
      }

      // The bootstrapper is a separate track: keep its pointer doc + the kept boot releases' binaries.
      state.referenced.add(keys.bootstrapperManifest(ch.channel));
      const allBoot = await db.listBootSeqs(env.DB, app, ch.channel);
      const bootPtr = await db.getBootPointer(env.DB, app, ch.channel);
      const keptBoot = new Set<number>(allBoot.slice(0, keep));
      if (bootPtr != null) keptBoot.add(bootPtr);
      for (const bseq of keptBoot) {
        const brow = await db.getBootstrapper(env.DB, app, ch.channel, bseq);
        if (!brow) continue;
        keptBootstrappers++;
        try {
          for (const k of bootstrapperKeysOf(JSON.parse(brow.manifest_json) as BootstrapperManifest)) state.referenced.add(k);
        } catch (e) {
          errors.push(`keep boot ${app}/${ch.channel}#${bseq}: ${asMessage(e)}`);
        }
      }
      for (const bseq of allBoot) {
        if (!keptBoot.has(bseq)) {
          if (!dryRun) await db.deleteBootstrapper(env.DB, app, ch.channel, bseq);
          dbDeleted++;
        }
      }
    }
  }

  // 2) Self-heal: push the current manifests to every MANIFEST mirror (best-effort per mirror).
  let replicated = 0;
  for (const mirror of manifestMirrors) {
    try {
      for (const app of apps) {
        const keys = cdnKeysFor(appPrefix(app, defApp));
        const config = await db.getConfig(env.DB, app);
        if (config) {
          if (!dryRun) await mirror.put(keys.config, config, "application/json");
          replicated++;
        }
        for (const ch of await db.allChannels(env.DB, app)) {
          const cur = await db.currentRelease(env.DB, app, ch.channel);
          if (cur) {
            if (!dryRun) await mirror.put(keys.release(ch.channel), cur.manifest_json, "application/json");
            replicated++;
          }
          const cb = await db.currentBootstrapper(env.DB, app, ch.channel);
          if (cb) {
            if (!dryRun) await mirror.put(keys.bootstrapperManifest(ch.channel), cb.manifest_json, "application/json");
            replicated++;
          }
        }
      }
    } catch (e) {
      errors.push(`manifest ${mirror.cfg.name}: ${asMessage(e)}`);
    }
  }

  // 3) Prune unreferenced artifacts on the CONTENT mirrors (manifests are never touched). Chunks are only
  //    pruned when their app's keep set was fully expanded, so we never delete one a live release needs.
  let deleted = 0;
  let listed = 0;
  let skippedChunks = 0;
  let unknownAppKeys = 0;
  const wouldDelete: string[] = [];
  for (const mirror of contentMirrors) {
    try {
      const keys = await mirror.list();
      listed += keys.length;
      for (const key of keys) {
        const owner = keyOwnerApp(key, defApp);
        const state = perApp.get(owner);
        if (!state) {
          unknownAppKeys++; // an app namespace this worker doesn't know — never touch it
          continue;
        }
        if (state.referenced.has(key)) continue;
        const local = key.slice(state.prefix.length); // app-local key for layout checks
        if (!isPrunableArtifact(local)) continue;
        if (isChunk(local) && !state.chunksExpanded) {
          skippedChunks++;
          continue;
        }
        if (dryRun) {
          if (wouldDelete.length < 100) wouldDelete.push(key);
        } else {
          await mirror.delete(key);
        }
        deleted++;
      }
    } catch (e) {
      errors.push(`content ${mirror.cfg.name}: ${asMessage(e)}`);
    }
  }

  return {
    apps: [...apps].sort(),
    channels: channelCount,
    keptReleases,
    keptBootstrappers,
    referencedKeys: [...perApp.values()].reduce((n, s) => n + s.referenced.size, 0),
    keepPerChannel: keep,
    replicated,
    listed,
    skippedChunks,
    unknownAppKeys,
    deleted: dryRun ? 0 : deleted,
    dryRun,
    wouldDeleteTotal: dryRun ? deleted : 0,
    wouldDelete: dryRun ? wouldDelete : [],
    dbDeleted: dryRun ? 0 : dbDeleted,
    errors,
  };
}

/** Reads a package.json from the first content mirror that has it; memoized across channels/releases. */
async function fetchPackageManifest(
  mirrors: S3Mirror[], key: string, cache: Map<string, PackageManifest | null>,
): Promise<PackageManifest | null> {
  if (cache.has(key)) return cache.get(key) ?? null;
  let result: PackageManifest | null = null;
  for (const m of mirrors) {
    try {
      const text = await m.get(key);
      if (text) {
        result = JSON.parse(text) as PackageManifest;
        break;
      }
    } catch {
      /* try the next mirror */
    }
  }
  cache.set(key, result);
  return result;
}

/** Prunability of an APP-LOCAL (prefix-stripped) key — the same layout rules for every app. */
function isPrunableArtifact(localKey: string): boolean {
  // Manifests (config / release.json / bootstrapper.json) are pointers, never pruned as artifacts.
  if (localKey === "config.json" || localKey.endsWith("/release.json") || localKey.endsWith("/bootstrapper.json")) return false;
  return isChunk(localKey) || localKey.endsWith("/package.json") || localKey.startsWith("bootstrapper/") || localKey.startsWith("macapp/");
}
