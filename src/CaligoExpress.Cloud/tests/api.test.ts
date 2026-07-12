import { beforeAll, describe, expect, test } from "bun:test";
import { createApp } from "../src/app.ts";
import { ensureSchema, getConfig, listApps, listReleaseSeqs } from "../src/db.ts";
import { openBunD1 } from "../src/db-bun.ts";
import { runGc } from "../src/gc.ts";
import type { BootstrapperManifest, Env, ReleaseManifest } from "../src/types.ts";

const TOKEN = "test-token";
let app: ReturnType<typeof createApp>;
let env: Env;

function release(channel: string, seq: number, packages: ReleaseManifest["packages"] = []): ReleaseManifest {
  return {
    schemaVersion: 1,
    channel,
    releaseSeq: seq,
    releaseId: `rel-${seq}`,
    createdAt: "2026-06-04T00:00:00Z",
    packages,
  };
}

function pkg(name: string, version: string, deps: string[] = []): ReleaseManifest["packages"][number] {
  return {
    name,
    version,
    required: true,
    deps,
    manifest: { key: `${name}/${version}/package.json`, hash: "m", size: 1 },
  };
}

function req(method: string, path: string, body: unknown, auth = true): Request {
  return new Request(`http://local${path}`, {
    method,
    headers: auth ? { authorization: `Bearer ${TOKEN}`, "content-type": "application/json" } : { "content-type": "application/json" },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

const post = (path: string, body: unknown, auth = true) => req("POST", path, body, auth);
const put = (path: string, body: unknown, auth = true) => req("PUT", path, body, auth);

beforeAll(async () => {
  const db = openBunD1(":memory:");
  await ensureSchema(db);
  env = { DB: db, PUBLISH_TOKEN: TOKEN, MIRRORS: "[]" };
  app = createApp();
});

describe("caligo-cloud api", () => {
  test("health", async () => {
    const res = await app.fetch(new Request("http://local/health"), env);
    expect(res.status).toBe(200);
    expect(await res.json()).toMatchObject({ ok: true });
  });

  test("publish requires auth", async () => {
    const res = await app.fetch(post("/publish", release("beta", 1), false), env);
    expect(res.status).toBe(401);
  });

  test("publish then serve current release", async () => {
    const pub = await app.fetch(post("/publish", release("beta", 1, [pkg("core", "1.0.0")])), env);
    expect(pub.status).toBe(200);
    expect(await pub.json()).toMatchObject({ ok: true, channel: "beta", seq: 1 });

    const got = await app.fetch(new Request("http://local/beta/release.json"), env);
    expect(got.status).toBe(200);
    const manifest = (await got.json()) as ReleaseManifest;
    expect(manifest.releaseSeq).toBe(1);
    expect(manifest.packages[0]!.name).toBe("core");
  });

  test("non-monotonic seq is rejected", async () => {
    const res = await app.fetch(post("/publish", release("beta", 1)), env);
    expect(res.status).toBe(409);
  });

  test("dependency cycle is rejected", async () => {
    const cyclic = release("beta", 2, [pkg("a", "1.0.0", ["b"]), pkg("b", "1.0.0", ["a"])]);
    const res = await app.fetch(post("/publish", cyclic), env);
    expect(res.status).toBe(409);
  });

  test("publish blocks downgrade and requires a version bump (force overrides)", async () => {
    const mk = (seq: number, pkgVer: string): ReleaseManifest => release("ver", seq, [pkg("core", pkgVer)]);

    expect((await app.fetch(post("/publish", mk(1, "1.0.0")), env)).status).toBe(200);
    expect((await app.fetch(post("/publish", mk(2, "1.0.0")), env)).status).toBe(409); // no bump
    expect((await app.fetch(post("/publish", mk(2, "0.9.0")), env)).status).toBe(409); // downgrade
    expect((await app.fetch(post("/publish", mk(2, "1.1.0")), env)).status).toBe(200); // bumped
    expect((await app.fetch(post("/publish", { release: mk(3, "1.1.0"), force: true }), env)).status).toBe(200); // forced
  });

  test("publish orders 4-part GitVersion versions by the 4th component", async () => {
    const mk = (seq: number, ver: string): ReleaseManifest => release("v4", seq, [pkg("core", ver)]);
    expect((await app.fetch(post("/publish", mk(1, "2.16.1.221")), env)).status).toBe(200);
    expect((await app.fetch(post("/publish", mk(2, "2.16.1.221")), env)).status).toBe(409); // identical -> no bump
    expect((await app.fetch(post("/publish", mk(2, "2.16.1.220")), env)).status).toBe(409); // 220 < 221 -> downgrade
    expect((await app.fetch(post("/publish", mk(2, "2.16.1.222")), env)).status).toBe(200); // 222 > 221 -> bump
  });

  test("bootstrapper is its own track: publish, serve, downgrade-block, rollback pointer", async () => {
    const boot = (seq: number, version: string) => ({
      schemaVersion: 1,
      channel: "bch",
      seq,
      version,
      createdAt: "2026-06-04T00:00:00Z",
      full: { key: `bootstrapper/${version}/bootstrapper.bin`, hash: "bb", size: 1 },
    });

    expect((await app.fetch(post("/publish-bootstrapper", boot(1, "1.0.0")), env)).status).toBe(200);
    expect((await app.fetch(post("/publish-bootstrapper", boot(2, "1.1.0")), env)).status).toBe(200);
    expect((await app.fetch(post("/publish-bootstrapper", boot(3, "0.9.0")), env)).status).toBe(409); // downgrade

    let cur = (await (await app.fetch(new Request("http://local/bch/bootstrapper.json"), env)).json()) as { version: string };
    expect(cur.version).toBe("1.1.0");

    // Roll the bootstrapper pointer back independently of any app release (kind: "boot").
    expect((await app.fetch(post("/rollback", { channel: "bch", seq: 1, kind: "boot" }), env)).status).toBe(200);
    cur = (await (await app.fetch(new Request("http://local/bch/bootstrapper.json"), env)).json()) as { version: string };
    expect(cur.version).toBe("1.0.0");

    // The app release pointer for this channel is untouched (no app release published here).
    expect((await app.fetch(new Request("http://local/bch/release.json"), env)).status).toBe(404);
  });

  test("publish seq 2, then rollback pointer to seq 1", async () => {
    expect((await app.fetch(post("/publish", release("beta", 2, [pkg("core", "2.0.0")])), env)).status).toBe(200);

    let current = (await (await app.fetch(new Request("http://local/beta/release.json"), env)).json()) as ReleaseManifest;
    expect(current.releaseSeq).toBe(2);

    const rb = await app.fetch(post("/rollback", { channel: "beta", seq: 1 }), env);
    expect(rb.status).toBe(200);

    current = (await (await app.fetch(new Request("http://local/beta/release.json"), env)).json()) as ReleaseManifest;
    expect(current.releaseSeq).toBe(1);
  });

  test("gc prunes D1 release rows beyond the keep window (keeps the live pointer)", async () => {
    for (let i = 1; i <= 7; i++) {
      const r = await app.fetch(post("/publish", release("gcdb", i, [pkg("core", `1.0.${i}`)])), env);
      expect(r.status).toBe(200);
    }
    expect((await listReleaseSeqs(env.DB, "argon", "gcdb")).length).toBe(7);

    // A dry run reports what it WOULD prune without touching the rows.
    const dry = await runGc(env, { dryRun: true });
    expect(dry.dryRun).toBe(true);
    expect(dry.dbDeleted).toBe(0);
    expect((await listReleaseSeqs(env.DB, "argon", "gcdb")).length).toBe(7);

    const report = await runGc(env);
    expect(report.dbDeleted).toBeGreaterThanOrEqual(2); // 7 - keep(5)

    const remaining = await listReleaseSeqs(env.DB, "argon", "gcdb");
    expect(remaining.length).toBe(5);
    expect(remaining).toContain(7); // live pointer kept
    expect(remaining).not.toContain(1); // oldest dropped
  });

  type SettingsView = {
    manifestMirrors: { name: string; accessKeyId: string; hasSecret: boolean }[];
    contentMirrors: { name: string; accessKeyId: string; hasSecret: boolean }[];
    keepPerChannel: number;
  };
  const getSettings = async (): Promise<SettingsView> =>
    (await (await app.fetch(new Request("http://local/admin/settings", { headers: { authorization: `Bearer ${TOKEN}` } }), env)).json()) as SettingsView;

  test("admin settings require auth, then round-trip content/manifest mirrors", async () => {
    expect((await app.fetch(new Request("http://local/admin/settings"), env)).status).toBe(401);

    const content = [{ name: "r2", endpoint: "https://x", bucket: "b", region: "auto", accessKeyId: "AKIA1234", secretAccessKey: "supersecret" }];
    expect((await app.fetch(put("/admin/settings/mirrors", { content }), env)).status).toBe(200);
    expect((await app.fetch(put("/admin/settings/keep", { keepPerChannel: 9 }), env)).status).toBe(200);

    const s = await getSettings();
    expect(s.keepPerChannel).toBe(9);
    expect(s.contentMirrors[0]!.name).toBe("r2");
    expect(s.contentMirrors[0]!.hasSecret).toBe(true);
    expect(s.contentMirrors[0]!.accessKeyId).toContain("1234"); // masked, not the raw key
    expect(s.contentMirrors[0]!.accessKeyId).not.toBe("AKIA1234");
    expect(s.manifestMirrors).toEqual([]);
  });

  test("editing a mirror with blank credentials preserves the stored secret", async () => {
    await app.fetch(put("/admin/settings/mirrors", { content: [{ name: "r2", endpoint: "https://EDITED", bucket: "b", region: "auto", accessKeyId: "", secretAccessKey: "" }] }), env);
    const s = await getSettings();
    expect(s.contentMirrors[0]!.hasSecret).toBe(true); // secret kept across the edit

    // Reset so later replicate-calling tests don't try to reach the fake mirror.
    await app.fetch(put("/admin/settings/mirrors", { content: [] }), env);
  });

  test("pinned keys can be added, are returned in full, and survive a blank-value edit", async () => {
    const read = async () => (await getSettings() as unknown as { pinnedKeys: Record<string, string> }).pinnedKeys;

    expect((await app.fetch(put("/admin/settings/pinned-keys", { keys: { k1: "BASE64PUBKEY==" } }), env)).status).toBe(200);
    expect((await read()).k1).toBe("BASE64PUBKEY=="); // value persisted and returned

    // Blank value keeps the stored key; a new one is added.
    await app.fetch(put("/admin/settings/pinned-keys", { keys: { k1: "", k2: "ANOTHER==" } }), env);
    const keys = await read();
    expect(keys.k1).toBe("BASE64PUBKEY==");
    expect(keys.k2).toBe("ANOTHER==");
  });

  test("presign returns upload URLs (machine token only) and never leaks creds", async () => {
    await app.fetch(put("/admin/settings/mirrors", {
      content: [{ name: "eu", endpoint: "https://eu.example.com", bucket: "argon-content", region: "auto", accessKeyId: "AKIA", secretAccessKey: "sec" }],
    }), env);

    // SSO/no-token clients are refused raw signing.
    expect((await app.fetch(post("/admin/presign", { keys: ["x"] }, false), env)).status).toBe(403);

    const res = await app.fetch(post("/admin/presign", { keys: ["core/1.0.0/core.zip"] }), env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as { items: { key: string; targets: { mirror: string; url: string }[] }[] };
    const url = body.items[0]!.targets[0]!.url;
    expect(body.items[0]!.key).toBe("core/1.0.0/core.zip");
    expect(url).toContain("argon-content/core/1.0.0/core.zip");
    expect(url).toContain("X-Amz-Signature");
    expect(url).not.toContain("sec"); // the secret key is never in the URL

    await app.fetch(put("/admin/settings/mirrors", { content: [] }), env);
  });

  test("me returns the machine identity for the publish token", async () => {
    const res = await app.fetch(new Request("http://local/me", { headers: { authorization: `Bearer ${TOKEN}` } }), env);
    expect(res.status).toBe(200);
    expect(((await res.json()) as { sub: string }).sub).toBe("ci");
  });

  test("config round-trips", async () => {
    expect((await app.fetch(new Request("http://local/config.json"), env)).status).toBe(404);

    const config = JSON.stringify({ schemaVersion: 1, mirrors: [], channels: {}, publicKeys: [], minBootstrapper: "1.0.0" });
    expect((await app.fetch(post("/config", config), env)).status).toBe(200);

    const got = await app.fetch(new Request("http://local/config.json"), env);
    expect(got.status).toBe(200);
    expect(((await got.json()) as { minBootstrapper: string }).minBootstrapper).toBe("1.0.0");
  });
});

// ── multi-app ─────────────────────────────────────────────────────────────────

function v2release(appName: string, channel: string, seq: number, ver = "1.0.0"): ReleaseManifest {
  return {
    schemaVersion: 1,
    app: appName,
    channel,
    releaseSeq: seq,
    releaseId: `rel-${appName}-${seq}`,
    createdAt: "2026-06-04T00:00:00Z",
    packages: [{
      name: "core", version: ver, required: true, deps: [],
      manifest: { key: `apps/${appName}/core/${ver}/package.json`, hash: "m", size: 1 },
    }],
  };
}

describe("caligo-cloud multi-app", () => {
  test("v2 publish + serve under /apps/:app, isolated from the default app's same-named channel", async () => {
    // Default app already has beta#1/#2 from the legacy tests above.
    const pub = await app.fetch(post("/apps/echo/publish", v2release("echo", "beta", 1)), env);
    expect(pub.status).toBe(200);
    expect(await pub.json()).toMatchObject({ ok: true, app: "echo", channel: "beta", seq: 1 });

    const got = await app.fetch(new Request("http://local/apps/echo/beta/release.json"), env);
    expect(got.status).toBe(200);
    expect(((await got.json()) as ReleaseManifest).releaseId).toBe("rel-echo-1");

    // The default app's beta channel is untouched (still the legacy rollback target, seq 1).
    const legacy = (await (await app.fetch(new Request("http://local/beta/release.json"), env)).json()) as ReleaseManifest;
    expect(legacy.releaseId).toBe("rel-1");
  });

  test("app scoping: missing/mismatched app field and foreign keys are rejected", async () => {
    // v2 manifests must declare their app (it is part of the signed payload).
    const noApp = { ...v2release("echo", "scoped", 1) };
    delete (noApp as { app?: string }).app;
    expect((await app.fetch(post("/apps/echo/publish", noApp), env)).status).toBe(409);

    // Mismatched app field.
    expect((await app.fetch(post("/apps/echo/publish", v2release("other", "scoped", 1)), env)).status).toBe(409);

    // Root-layout (default-app) keys inside a v2 release.
    const rootKeys = v2release("echo", "scoped", 1);
    rootKeys.packages[0]!.manifest.key = "core/1.0.0/package.json";
    expect((await app.fetch(post("/apps/echo/publish", rootKeys), env)).status).toBe(409);

    // apps/-prefixed keys inside a legacy (default-app) release.
    const stray = release("scoped", 1, [pkg("core", "1.0.0")]);
    stray.packages[0]!.manifest.key = "apps/echo/core/1.0.0/package.json";
    expect((await app.fetch(post("/publish", stray), env)).status).toBe(409);

    // Invalid app slug.
    expect((await app.fetch(post("/apps/UPPER/publish", v2release("UPPER", "x", 1)), env)).status).toBe(400);
  });

  test("per-app config and bootstrapper tracks", async () => {
    const config = JSON.stringify({ schemaVersion: 1, mirrors: [], channels: {}, publicKeys: [], minBootstrapper: "2.0.0" });
    expect((await app.fetch(post("/apps/echo/config", config), env)).status).toBe(200);
    expect(((await (await app.fetch(new Request("http://local/apps/echo/config.json"), env)).json()) as { minBootstrapper: string }).minBootstrapper).toBe("2.0.0");
    // The default app's config (set in the legacy tests) is untouched.
    expect(((await (await app.fetch(new Request("http://local/config.json"), env)).json()) as { minBootstrapper: string }).minBootstrapper).toBe("1.0.0");

    const boot: BootstrapperManifest = {
      schemaVersion: 1, app: "echo", channel: "beta", seq: 1, version: "1.0.0", createdAt: "2026-06-04T00:00:00Z",
      full: { key: "apps/echo/bootstrapper/1.0.0/bootstrapper.bin", hash: "b", size: 1 },
    };
    expect((await app.fetch(post("/apps/echo/publish-bootstrapper", boot), env)).status).toBe(200);
    const served = (await (await app.fetch(new Request("http://local/apps/echo/beta/bootstrapper.json"), env)).json()) as BootstrapperManifest;
    expect(served.version).toBe("1.0.0");
    // No cross-talk with the default app's bootstrapper channel of the same name.
    expect((await app.fetch(new Request("http://local/beta/bootstrapper.json"), env)).status).toBe(404);
  });

  test("per-app tokens: scoped publish, no cross-app or global access", async () => {
    // Only master/SSO can mint tokens.
    expect((await app.fetch(put("/admin/apps/echo/token", { token: "echo-secret" }), env)).status).toBe(200);

    const asEcho = (path: string, body: unknown) =>
      new Request(`http://local${path}`, {
        method: "POST",
        headers: { authorization: "Bearer echo-secret", "content-type": "application/json" },
        body: JSON.stringify(body),
      });

    // The echo token publishes to echo...
    expect((await app.fetch(asEcho("/apps/echo/publish", v2release("echo", "beta", 2, "1.1.0")), env)).status).toBe(200);
    // ...but not to the default app or another app.
    expect((await app.fetch(asEcho("/publish", release("beta", 9)), env)).status).toBe(401);
    expect((await app.fetch(asEcho("/apps/other/publish", v2release("other", "beta", 1)), env)).status).toBe(401);

    // An app token cannot rotate tokens (not even its own) or edit global settings.
    const echoPut = (path: string, body: unknown) =>
      new Request(`http://local${path}`, {
        method: "PUT",
        headers: { authorization: "Bearer echo-secret", "content-type": "application/json" },
        body: JSON.stringify(body),
      });
    expect((await app.fetch(echoPut("/admin/apps/echo/token", { token: "hijack" }), env)).status).toBe(403);
    // Global settings live on default-app routes, where an echo token doesn't even authenticate.
    expect((await app.fetch(echoPut("/admin/settings/mirrors", { content: [] }), env)).status).toBe(401);
    expect((await app.fetch(echoPut("/admin/settings/keep", { keepPerChannel: 1 }), env)).status).toBe(401);
  });

  test("presign is fenced to the route app's namespace", async () => {
    await app.fetch(put("/admin/settings/mirrors", {
      content: [{ name: "eu", endpoint: "https://eu.example.com", bucket: "argon-content", region: "auto", accessKeyId: "AKIA", secretAccessKey: "sec" }],
    }), env);

    // Master token, but the key belongs to another app's namespace → refused.
    expect((await app.fetch(post("/apps/echo/admin/presign", { keys: ["core/1.0.0/core.zip"] }), env)).status).toBe(403);
    expect((await app.fetch(post("/admin/presign", { keys: ["apps/echo/chunks/ab/abcd"] }), env)).status).toBe(403);

    // In-namespace keys sign fine on both route shapes.
    expect((await app.fetch(post("/apps/echo/admin/presign", { keys: ["apps/echo/chunks/ab/abcd"] }), env)).status).toBe(200);
    expect((await app.fetch(post("/admin/presign", { keys: ["core/1.0.0/core.zip"] }), env)).status).toBe(200);

    await app.fetch(put("/admin/settings/mirrors", { content: [] }), env);
  });

  test("per-app pinned keys are independent", async () => {
    expect((await app.fetch(put("/apps/echo/admin/settings/pinned-keys", { keys: { e1: "ECHOKEY==" } }), env)).status).toBe(200);
    const echoSettings = (await (await app.fetch(new Request("http://local/apps/echo/admin/settings", { headers: { authorization: `Bearer ${TOKEN}` } }), env)).json()) as { pinnedKeys: Record<string, string> };
    expect(echoSettings.pinnedKeys.e1).toBe("ECHOKEY==");

    const defSettings = (await (await app.fetch(new Request("http://local/admin/settings", { headers: { authorization: `Bearer ${TOKEN}` } }), env)).json()) as { pinnedKeys: Record<string, string> };
    expect(defSettings.pinnedKeys.e1).toBeUndefined();
  });

  test("admin/apps lists every app with token state", async () => {
    const res = await app.fetch(new Request("http://local/admin/apps", { headers: { authorization: `Bearer ${TOKEN}` } }), env);
    expect(res.status).toBe(200);
    const body = (await res.json()) as { defaultApp: string; apps: { app: string; isDefault: boolean; hasToken: boolean }[] };
    expect(body.defaultApp).toBe("argon");
    const echo = body.apps.find((a) => a.app === "echo");
    expect(echo).toMatchObject({ isDefault: false, hasToken: true });
    expect(body.apps.find((a) => a.app === "argon")).toMatchObject({ isDefault: true });
  });
});

describe("legacy database migration", () => {
  test("a single-app database is re-homed under the default app in place", async () => {
    const db = openBunD1(":memory:");
    // Old single-app shape, as deployed before multi-app.
    await db.prepare(`CREATE TABLE releases (channel TEXT NOT NULL, seq INTEGER NOT NULL, release_id TEXT NOT NULL, created_at TEXT NOT NULL, manifest_json TEXT NOT NULL, published_at TEXT NOT NULL, PRIMARY KEY (channel, seq))`).run();
    await db.prepare(`CREATE TABLE channels (channel TEXT PRIMARY KEY, current_seq INTEGER NOT NULL, updated_at TEXT NOT NULL)`).run();
    await db.prepare(`ALTER TABLE channels ADD COLUMN boot_seq INTEGER`).run();
    await db.prepare(`CREATE TABLE bootstrappers (channel TEXT NOT NULL, seq INTEGER NOT NULL, version TEXT NOT NULL, created_at TEXT NOT NULL, manifest_json TEXT NOT NULL, published_at TEXT NOT NULL, PRIMARY KEY (channel, seq))`).run();
    await db.prepare(`CREATE TABLE config (id INTEGER PRIMARY KEY CHECK (id = 1), config_json TEXT NOT NULL, updated_at TEXT NOT NULL)`).run();

    const manifest = JSON.stringify(release("live", 3));
    await db.prepare(`INSERT INTO releases VALUES ('live', 3, 'rel-3', 't', ?, 't')`).bind(manifest).run();
    await db.prepare(`INSERT INTO channels (channel, current_seq, updated_at, boot_seq) VALUES ('live', 3, 't', NULL)`).run();
    await db.prepare(`INSERT INTO config VALUES (1, '{"minBootstrapper":"1.0.0"}', 't')`).run();

    await ensureSchema(db, "argon");

    expect(await listApps(db)).toEqual(["argon"]);
    expect(await listReleaseSeqs(db, "argon", "live")).toEqual([3]);
    expect(await getConfig(db, "argon")).toBe('{"minBootstrapper":"1.0.0"}');

    // The migrated database serves the legacy routes unchanged.
    const migratedEnv: Env = { DB: db, PUBLISH_TOKEN: TOKEN, MIRRORS: "[]" };
    const got = await app.fetch(new Request("http://local/live/release.json"), migratedEnv);
    expect(got.status).toBe(200);
    expect(((await got.json()) as ReleaseManifest).releaseId).toBe("rel-3");

    // Re-running the migration is a no-op.
    await ensureSchema(db, "argon");
    expect(await listReleaseSeqs(db, "argon", "live")).toEqual([3]);
  });
});
