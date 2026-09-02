// The control-plane HTTP API (Hono). Runtime-agnostic across local Bun and Cloudflare Workers.
// Public reads serve the signed manifests; token/SSO-guarded writes record releases and move channel
// pointers; the /admin/* surface powers the Vue admin (overview, releases, runtime settings + secrets).
//
// Multi-app: every route exists in two shapes — the LEGACY un-prefixed shape, bound to the default app
// (shipped Argon bootstrappers hard-code those paths), and `/apps/:app/...` for every other app. One
// handler serves both; the app comes from the route (or defaults). Storage keys for non-default apps
// live under `apps/<app>/`, so all apps share the same hosts, buckets and worker.

import { Hono } from "hono";
import { cors } from "hono/cors";
import type { Context, Handler, MiddlewareHandler } from "hono";
import { isAppCi, requireAuth, requireMachine } from "./auth.ts";
import { verifySignedManifest } from "./canonical.ts";
import * as db from "./db.ts";
import { appPrefix, cdnKeysFor, keyBelongsToApp } from "./keys.ts";
import { runGc } from "./gc.ts";
import {
  getAppTokens,
  getContentMirrorConfigs,
  getGcDryRun,
  getKeepPerChannel,
  getManifestMirrorConfigs,
  getPinnedKeys,
  manifestMirrorsFor,
  mergeMirrorSecrets,
  pinnedKeysKeyFor,
  presignUploads,
  redactMirrors,
  setAppToken,
  setMirrorConfigs,
  SettingsKeys,
} from "./settings.ts";
import {
  AegisDefaults,
  defaultAppOf,
  isValidApp,
  type BootstrapperManifest,
  type Env,
  type ReleaseManifest,
  type Vars,
} from "./types.ts";
import { assertAcyclicDeps, assertBootstrapperProgress, assertMonotonic, assertVersionProgress, ValidationError } from "./validate.ts";

type HonoEnv = { Bindings: Env; Variables: Vars };
type Ctx = Context<HonoEnv>;

/** The app a request addresses: the validated `:app` param, or the default app on legacy routes. */
function appOf(c: Ctx): string | null {
  const p = c.req.param("app");
  if (p === undefined) return defaultAppOf(c.env);
  return isValidApp(p) ? p : null;
}

const badApp = (c: Ctx) => c.json({ error: "invalid app slug" }, 400);

export function createApp(): Hono<HonoEnv> {
  const app = new Hono<HonoEnv>();

  // Surface unhandled errors as JSON instead of a bare 500, so failures (e.g. GC hitting a dead mirror)
  // are diagnosable from the response body.
  app.onError((err, c) => {
    console.error("unhandled error", err);
    return c.json({ error: err instanceof Error ? err.message : String(err) }, 500);
  });

  app.use("*", cors({
    origin: (origin, c) => {
      const allowed = (c.env as Env).ADMIN_ORIGIN ?? AegisDefaults.adminOrigin;
      if (!origin) return allowed;
      return origin === allowed || origin.startsWith("http://localhost") ? origin : allowed;
    },
    allowHeaders: ["Authorization", "Content-Type"],
    allowMethods: ["GET", "POST", "PUT", "OPTIONS"],
    maxAge: 600,
  }));

  app.get("/health", (c) => c.json({ ok: true, service: "caligo-cloud" }));

  // Registers a handler at the legacy (default-app) path AND at /apps/:app + the same path.
  type RouteHandler = Handler<HonoEnv> | MiddlewareHandler<HonoEnv>;
  const dual = (method: string, path: string, ...handlers: RouteHandler[]) => {
    app.on(method, path, ...(handlers as [RouteHandler, ...RouteHandler[]]));
    app.on(method, `/apps/:app${path}`, ...(handlers as [RouteHandler, ...RouteHandler[]]));
  };

  // ── Public reads (served to clients and the CDN edge) ────────────────────────
  dual("GET", "/config.json", async (c) => {
    const a = appOf(c);
    if (!a) return badApp(c);
    const config = await db.getConfig(c.env.DB, a);
    return config
      ? c.body(config, 200, { "content-type": "application/json" })
      : c.json({ error: "config not set" }, 404);
  });

  dual("GET", "/:channel/release.json", async (c) => {
    const a = appOf(c);
    if (!a) return badApp(c);
    const current = await db.currentRelease(c.env.DB, a, c.req.param("channel") ?? "");
    return current
      ? c.body(current.manifest_json, 200, { "content-type": "application/json" })
      : c.json({ error: "no release for channel" }, 404);
  });

  dual("GET", "/:channel/bootstrapper.json", async (c) => {
    const a = appOf(c);
    if (!a) return badApp(c);
    const cur = await db.currentBootstrapper(c.env.DB, a, c.req.param("channel") ?? "");
    return cur
      ? c.body(cur.manifest_json, 200, { "content-type": "application/json" })
      : c.json({ error: "no bootstrapper for channel" }, 404);
  });

  // ── Control-plane writes (publish token or SSO write scope) ──────────────────
  dual("POST", "/publish", requireAuth(), publish);
  dual("POST", "/publish-bootstrapper", requireAuth(), publishBootstrapper);
  dual("POST", "/promote", requireAuth(), setPointerHandler);
  dual("POST", "/rollback", requireAuth(), setPointerHandler);
  dual("POST", "/config", requireAuth(), putConfig);
  app.post("/gc", requireAuth(), gcHandler);

  // ── Admin surface for the Vue UI ─────────────────────────────────────────────
  app.get("/me", requireAuth(), (c) => {
    const u = c.get("user");
    return c.json({
      sub: u.sub, name: u.name, email: u.email,
      preferredUsername: u.preferred_username, roles: u.roles, scope: u.scope,
    });
  });

  app.get("/admin/apps", requireAuth(), adminApps);
  app.put("/admin/apps/:app/token", requireAuth(), adminSetAppToken);

  dual("GET", "/admin/overview", requireAuth(), adminOverview);
  dual("GET", "/admin/channels", requireAuth(), async (c) => {
    const a = appOf(c);
    if (!a) return badApp(c);
    return c.json(await db.allChannels(c.env.DB, a));
  });
  dual("GET", "/admin/releases/:channel", requireAuth(), adminReleases);
  dual("GET", "/admin/release/:channel/:seq", requireAuth(), adminReleaseDetail);
  dual("GET", "/admin/bootstrappers/:channel", requireAuth(), adminBootstrappers);

  dual("GET", "/admin/settings", requireAuth(), adminGetSettings);

  // Presigned upload URLs for publishers — the worker signs them so S3 credentials never leave it.
  dual("POST", "/admin/presign", requireMachine(), adminPresign);

  app.put("/admin/settings/mirrors", requireAuth(), adminSetMirrors);
  app.put("/admin/settings/keep", requireAuth(), adminSetKeep);
  app.put("/admin/settings/gc", requireAuth(), adminSetGcDryRun);
  dual("PUT", "/admin/settings/pinned-keys", requireAuth(), adminSetPinnedKeys);
  app.post("/admin/gc", requireAuth(), gcHandler);

  return app;
}

// ── write handlers ─────────────────────────────────────────────────────────────

/**
 * Signature gate for publishes. Signed delivery is the core security property, so the failure modes are
 * LOUD instead of silent:
 *  - no pinned keys for the app → 403 telling the operator to pin the key first (a k1-in-the-wrong-app
 *    setup mistake used to sail through unverified). Dev workers opt out with ALLOW_UNSIGNED=1.
 *  - unsigned manifest / unknown keyId → 403 naming the keyIds the app actually has, so a typo'd
 *    --key-id is diagnosable from the CI log.
 */
async function assertSigned(
  c: Ctx, app: string, manifest: { signature?: { keyId: string; alg: string; sig: string } },
): Promise<Response | null> {
  const pinned = await getPinnedKeys(c.env, app, defaultAppOf(c.env));

  if (Object.keys(pinned).length === 0) {
    if (c.env.ALLOW_UNSIGNED === "1") return null;
    return c.json({
      error: `no pinned signing keys for app '${app}' — pin its public key first (admin → Settings → ` +
        `Trusted signing keys, with the app selected), or set ALLOW_UNSIGNED=1 on a dev worker`,
    }, 403);
  }

  if (!manifest.signature) {
    return c.json({ error: `manifest is unsigned, but app '${app}' has pinned signing keys` }, 403);
  }
  if (!pinned[manifest.signature.keyId]) {
    return c.json({
      error: `manifest signed with unknown keyId '${manifest.signature.keyId}' — ` +
        `pinned keys for app '${app}': ${Object.keys(pinned).join(", ")}`,
    }, 403);
  }
  if (!(await verifySignedManifest(manifest, pinned))) {
    return c.json({ error: "signature verification failed" }, 403);
  }
  return null;
}

/** Rejects a manifest whose `app` field or referenced artifact keys point outside the route's app. */
function assertAppScope(
  c: Ctx, app: string, manifestApp: string | undefined, keys: (string | undefined)[],
): Response | null {
  const defApp = defaultAppOf(c.env);
  // Legacy manifests (default app, old tooling) may omit `app`; every other app must declare it — the
  // field is inside the signed payload, so a manifest can't be replayed into another app's namespace.
  if (manifestApp !== undefined && manifestApp !== app) {
    return c.json({ error: `manifest is for app '${manifestApp}', not '${app}'` }, 409);
  }
  if (app !== defApp && manifestApp === undefined) {
    return c.json({ error: `manifest must carry app: '${app}' (it is part of the signed payload)` }, 409);
  }
  for (const key of keys) {
    if (key !== undefined && !keyBelongsToApp(key, app, defApp)) {
      return c.json({ error: `artifact key '${key}' is outside app '${app}'s namespace` }, 409);
    }
  }
  return null;
}

async function publish(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);

  let release: ReleaseManifest;
  let force = false;
  try {
    const body = (await c.req.json()) as { release?: ReleaseManifest; force?: boolean } & ReleaseManifest;
    release = body.release ?? body;
    force = body.force === true;
  } catch {
    return c.json({ error: "invalid JSON body" }, 400);
  }

  const scopeError = assertAppScope(c, a, release.app, [
    ...(release.packages ?? []).map((p) => p.manifest.key),
    release.bootstrapper?.full.key,
    release.macApp?.artifact.key,
  ]);
  if (scopeError) return scopeError;

  try {
    assertAcyclicDeps(release.packages ?? []);
    const max = await db.maxSeq(c.env.DB, a, release.channel);
    assertMonotonic(max, release.releaseSeq); // can't overwrite or go below an existing seq
    if (max > 0) {
      const prevRow = await db.getRelease(c.env.DB, a, release.channel, max);
      if (prevRow) assertVersionProgress(JSON.parse(prevRow.manifest_json) as ReleaseManifest, release, !force);
    }
  } catch (e) {
    if (e instanceof ValidationError) return c.json({ error: e.message }, 409);
    throw e;
  }

  const sigError = await assertSigned(c, a, release);
  if (sigError) return sigError;

  const json = JSON.stringify(release);
  await db.insertRelease(c.env.DB, a, release.channel, release.releaseSeq, release.releaseId, release.createdAt, json);
  await db.setPointer(c.env.DB, a, release.channel, release.releaseSeq);
  const keys = cdnKeysFor(appPrefix(a, defaultAppOf(c.env)));
  const rep = await replicate(c.env, keys.release(release.channel), json);

  return c.json({ ok: true, app: a, channel: release.channel, seq: release.releaseSeq, replicated: rep.ok, replicationErrors: rep.errors });
}

// Publishes a bootstrapper to its own track (separate seq + pointer from app releases).
async function publishBootstrapper(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);

  let boot: BootstrapperManifest;
  let force = false;
  try {
    const body = (await c.req.json()) as { bootstrapper?: BootstrapperManifest; force?: boolean } & BootstrapperManifest;
    boot = body.bootstrapper ?? body;
    force = body.force === true;
  } catch {
    return c.json({ error: "invalid JSON body" }, 400);
  }

  const scopeError = assertAppScope(c, a, boot.app, [boot.full?.key, boot.macFull?.key]);
  if (scopeError) return scopeError;

  try {
    const max = await db.maxBootSeq(c.env.DB, a, boot.channel);
    assertMonotonic(max, boot.seq);
    if (!force && max > 0) {
      const prev = await db.getBootstrapper(c.env.DB, a, boot.channel, max);
      if (prev) assertBootstrapperProgress(prev.version, boot.version);
    }
  } catch (e) {
    if (e instanceof ValidationError) return c.json({ error: e.message }, 409);
    throw e;
  }

  const sigError = await assertSigned(c, a, boot);
  if (sigError) return sigError;

  const json = JSON.stringify(boot);
  await db.insertBootstrapper(c.env.DB, a, boot.channel, boot.seq, boot.version, boot.createdAt, json);
  await db.setBootPointer(c.env.DB, a, boot.channel, boot.seq);
  const keys = cdnKeysFor(appPrefix(a, defaultAppOf(c.env)));
  const rep = await replicate(c.env, keys.bootstrapperManifest(boot.channel), json);

  return c.json({ ok: true, app: a, channel: boot.channel, seq: boot.seq, version: boot.version, replicated: rep.ok, replicationErrors: rep.errors });
}

// Move the app-release pointer (default) or the bootstrapper pointer (kind: "boot") to a published seq.
async function setPointerHandler(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);

  let channel: string;
  let seq: number;
  let kind: "app" | "boot";
  try {
    const body = (await c.req.json()) as { channel: string; seq: number; kind?: string };
    channel = body.channel;
    seq = Number(body.seq);
    kind = body.kind === "boot" ? "boot" : "app";
  } catch {
    return c.json({ error: "invalid JSON body" }, 400);
  }

  const keys = cdnKeysFor(appPrefix(a, defaultAppOf(c.env)));

  if (kind === "boot") {
    const row = await db.getBootstrapper(c.env.DB, a, channel, seq);
    if (!row) return c.json({ error: `no bootstrapper ${channel}#${seq}` }, 404);
    await db.setBootPointer(c.env.DB, a, channel, seq);
    const rep = await replicate(c.env, keys.bootstrapperManifest(channel), row.manifest_json);
    return c.json({ ok: true, app: a, channel, seq, kind, replicated: rep.ok, replicationErrors: rep.errors });
  }

  const row = await db.getRelease(c.env.DB, a, channel, seq);
  if (!row) return c.json({ error: `no release ${channel}#${seq}` }, 404);
  await db.setPointer(c.env.DB, a, channel, seq);
  const rep = await replicate(c.env, keys.release(channel), row.manifest_json);
  return c.json({ ok: true, app: a, channel, seq, kind, replicated: rep.ok, replicationErrors: rep.errors });
}

async function putConfig(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);
  const text = await c.req.text();
  if (!text) return c.json({ error: "empty config body" }, 400);
  await db.setConfig(c.env.DB, a, text);
  const keys = cdnKeysFor(appPrefix(a, defaultAppOf(c.env)));
  const rep = await replicate(c.env, keys.config, text);
  return c.json({ ok: true, app: a, replicated: rep.ok, replicationErrors: rep.errors });
}

async function gcHandler(c: Ctx): Promise<Response> {
  let dryRun = c.req.query("dryRun") === "1" || c.req.query("dryRun") === "true";
  try {
    const body = (await c.req.json()) as { dryRun?: boolean };
    if (typeof body.dryRun === "boolean") dryRun = body.dryRun;
  } catch {
    /* no/invalid body → query param only */
  }
  return c.json(await runGc(c.env, { dryRun }));
}

// ── admin handlers ───────────────────────────────────────────────────────────

async function adminApps(c: Ctx): Promise<Response> {
  const defApp = defaultAppOf(c.env);
  const known = new Set(await db.listApps(c.env.DB));
  known.add(defApp);
  const tokens = await getAppTokens(c.env);
  const apps = [...known].sort().map((app) => ({
    app,
    isDefault: app === defApp,
    hasToken: !!tokens[app],
  }));
  return c.json({ defaultApp: defApp, apps });
}

// Body: { token: string } to set/rotate, { token: "" } to remove. Master token / SSO only — a per-app
// CI token must not be able to mint or rotate tokens (not even its own).
async function adminSetAppToken(c: Ctx): Promise<Response> {
  if (isAppCi(c.get("user"))) return c.json({ error: "app tokens cannot manage tokens" }, 403);
  const target = c.req.param("app") ?? "";
  if (!isValidApp(target)) return badApp(c);
  let token: string;
  try {
    const body = (await c.req.json()) as { token?: string };
    token = typeof body.token === "string" ? body.token.trim() : "";
  } catch {
    return c.json({ error: "invalid JSON body" }, 400);
  }
  await setAppToken(c.env, target, token);
  return c.json({ ok: true, app: target, hasToken: !!token });
}

async function adminOverview(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);
  const channels = await db.allChannels(c.env.DB, a);
  const items = [];
  for (const ch of channels) {
    const boot = await db.currentBootstrapper(c.env.DB, a, ch.channel);
    const cur = await db.currentRelease(c.env.DB, a, ch.channel);
    const base = { channel: ch.channel, currentSeq: ch.current_seq, bootstrapper: boot?.version ?? null };
    if (!cur) {
      items.push({ ...base, release: null });
      continue;
    }
    const m = JSON.parse(cur.manifest_json) as ReleaseManifest;
    items.push({
      ...base,
      release: {
        releaseId: m.releaseId,
        releaseSeq: m.releaseSeq,
        createdAt: m.createdAt,
        packages: m.packages.map((p) => ({ name: p.name, version: p.version })),
        mac: m.macApp ? `${m.macApp.bundleName}@${m.macApp.version}` : null,
      },
    });
  }
  return c.json({ app: a, channels: items });
}

async function adminBootstrappers(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);
  const channel = c.req.param("channel") ?? "";
  const seqs = await db.listBootSeqs(c.env.DB, a, channel);
  const pointer = await db.getBootPointer(c.env.DB, a, channel);
  const bootstrappers = [];
  for (const seq of seqs) {
    const row = await db.getBootstrapper(c.env.DB, a, channel, seq);
    if (!row) continue;
    let win = true;
    let mac = false;
    try {
      const m = JSON.parse(row.manifest_json) as BootstrapperManifest;
      win = !!m.full;
      mac = !!m.macFull;
    } catch { /* keep defaults */ }
    bootstrappers.push({ seq, version: row.version, createdAt: row.created_at, publishedAt: row.published_at, current: seq === pointer, win, mac });
  }
  return c.json({ app: a, channel, current: pointer, bootstrappers });
}

async function adminReleases(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);
  const channel = c.req.param("channel") ?? "";
  const seqs = await db.listReleaseSeqs(c.env.DB, a, channel);
  const pointer = await db.getPointer(c.env.DB, a, channel);
  const releases = [];
  for (const seq of seqs) {
    const row = await db.getRelease(c.env.DB, a, channel, seq);
    if (!row) continue;
    const m = JSON.parse(row.manifest_json) as ReleaseManifest;
    releases.push({
      seq,
      releaseId: m.releaseId,
      createdAt: m.createdAt,
      publishedAt: row.published_at,
      current: seq === pointer,
      packages: m.packages.map((p) => `${p.name}@${p.version}`),
      // The macOS whole-app payload (empty `packages`), so mac releases aren't shown blank.
      mac: m.macApp ? `${m.macApp.bundleName}@${m.macApp.version}` : null,
    });
  }
  return c.json({ app: a, channel, current: pointer, releases });
}

async function adminReleaseDetail(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);
  const row = await db.getRelease(c.env.DB, a, c.req.param("channel") ?? "", Number(c.req.param("seq")));
  return row
    ? c.body(row.manifest_json, 200, { "content-type": "application/json" })
    : c.json({ error: "not found" }, 404);
}

async function adminGetSettings(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);
  return c.json({
    app: a,
    manifestMirrors: redactMirrors(await getManifestMirrorConfigs(c.env)),
    contentMirrors: redactMirrors(await getContentMirrorConfigs(c.env)),
    keepPerChannel: await getKeepPerChannel(c.env),
    gcDryRun: await getGcDryRun(c.env),
    // Pinned keys are PUBLIC keys (not secrets) — return their full values so the UI can show them.
    pinnedKeys: await getPinnedKeys(c.env, a, defaultAppOf(c.env)),
  });
}

// Body: { dryRun: boolean } — when set, the GC CRON only reports what it would delete (rollout safety
// switch for storage-layout changes). Manual POST /gc calls can still pass their own dryRun explicitly.
async function adminSetGcDryRun(c: Ctx): Promise<Response> {
  if (isAppCi(c.get("user"))) return c.json({ error: "app tokens cannot edit global settings" }, 403);
  const body = (await c.req.json()) as { dryRun?: boolean };
  if (typeof body.dryRun !== "boolean") return c.json({ error: "expected { dryRun: boolean }" }, 400);
  await db.setSetting(c.env.DB, SettingsKeys.gcDryRun, body.dryRun ? "1" : "0");
  return c.json({ ok: true, gcDryRun: body.dryRun });
}

// Body: { manifest?: MirrorConfig[], content?: MirrorConfig[] }. Blank credentials are preserved from
// the currently stored mirror of the same name, so non-secret fields can be edited without re-entry.
// Mirrors are WORKER-GLOBAL (all apps share hosts/buckets) — so app-scoped CI tokens may not edit them.
async function adminSetMirrors(c: Ctx): Promise<Response> {
  if (isAppCi(c.get("user"))) return c.json({ error: "app tokens cannot edit global settings" }, 403);
  const body = (await c.req.json()) as { manifest?: unknown; content?: unknown };

  if (Array.isArray(body.manifest)) {
    const merged = mergeMirrorSecrets(body.manifest, await getManifestMirrorConfigs(c.env));
    await setMirrorConfigs(c.env, SettingsKeys.manifestMirrors, merged);
  }
  if (Array.isArray(body.content)) {
    const merged = mergeMirrorSecrets(body.content, await getContentMirrorConfigs(c.env));
    await setMirrorConfigs(c.env, SettingsKeys.contentMirrors, merged);
  }
  if (!Array.isArray(body.manifest) && !Array.isArray(body.content)) {
    return c.json({ error: "expected { manifest?: [...], content?: [...] }" }, 400);
  }
  return c.json({ ok: true });
}

async function adminSetKeep(c: Ctx): Promise<Response> {
  if (isAppCi(c.get("user"))) return c.json({ error: "app tokens cannot edit global settings" }, 403);
  const body = (await c.req.json()) as { keepPerChannel?: number };
  const n = Number(body.keepPerChannel);
  if (!Number.isFinite(n) || n < 1) return c.json({ error: "keepPerChannel must be >= 1" }, 400);
  await db.setSetting(c.env.DB, SettingsKeys.keepPerChannel, String(Math.floor(n)));
  return c.json({ ok: true, keepPerChannel: Math.floor(n) });
}

// Body: { kind?: "content"|"manifest", keys: string[], expiresIn?: seconds, skipExisting?: boolean }.
// Every requested key must lie inside the route app's namespace — this is what stops one app's CI
// (holding only its own token) from overwriting another app's artifacts.
async function adminPresign(c: Ctx): Promise<Response> {
  const a = appOf(c);
  if (!a) return badApp(c);
  const body = (await c.req.json()) as { kind?: string; keys?: string[]; expiresIn?: number; skipExisting?: boolean };
  if (!Array.isArray(body.keys) || body.keys.length === 0) {
    return c.json({ error: "expected { keys: string[] }" }, 400);
  }
  const defApp = defaultAppOf(c.env);
  for (const key of body.keys) {
    if (typeof key !== "string" || !keyBelongsToApp(key, a, defApp)) {
      return c.json({ error: `key '${key}' is outside app '${a}'s namespace` }, 403);
    }
  }
  const kind = body.kind === "manifest" ? "manifest" : "content";
  const expiresIn = Math.min(Math.max(Number(body.expiresIn ?? 900), 60), 3600);
  const items = await presignUploads(c.env, kind, body.keys, expiresIn, body.skipExisting === true);
  return c.json({ expiresIn, items });
}

// Trust anchors are per app but managed by operators only (master token / SSO) — never by CI tokens.
async function adminSetPinnedKeys(c: Ctx): Promise<Response> {
  if (isAppCi(c.get("user"))) return c.json({ error: "app tokens cannot edit pinned keys" }, 403);
  const a = appOf(c);
  if (!a) return badApp(c);
  const body = (await c.req.json()) as { keys?: Record<string, string> };
  if (!body.keys || typeof body.keys !== "object") return c.json({ error: "expected { keys: {id: base64} }" }, 400);

  // Blank values keep the stored key for that id (the UI can't read values back); empty ids are dropped.
  const existing = await getPinnedKeys(c.env, a, defaultAppOf(c.env));
  const merged: Record<string, string> = {};
  for (const [id, val] of Object.entries(body.keys)) {
    const v = typeof val === "string" && val.trim() ? val.trim() : existing[id] ?? "";
    if (v) merged[id] = v;
  }
  await db.setSetting(c.env.DB, pinnedKeysKeyFor(a, defaultAppOf(c.env)), JSON.stringify(merged));
  return c.json({ ok: true, app: a, keyIds: Object.keys(merged) });
}

// ── helpers ────────────────────────────────────────────────────────────────────

// Manifests (config.json + release.json) are replicated to the MANIFEST mirrors. Best-effort: the D1
// store is the source of truth (the worker serves manifests from it), so a misconfigured/unreachable
// mirror reports an error instead of failing the whole request. GC re-heals mirrors later.
async function replicate(env: Env, key: string, json: string): Promise<{ ok: number; errors: string[] }> {
  const errors: string[] = [];
  let mirrors: Awaited<ReturnType<typeof manifestMirrorsFor>>;
  try {
    mirrors = await manifestMirrorsFor(env);
  } catch (e) {
    return { ok: 0, errors: [`mirror init: ${asMessage(e)}`] };
  }

  let ok = 0;
  for (const mirror of mirrors) {
    try {
      await mirror.put(key, json, "application/json");
      ok++;
    } catch (e) {
      errors.push(`${mirror.cfg.name}: ${asMessage(e)}`);
    }
  }
  return { ok, errors };
}

function asMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
