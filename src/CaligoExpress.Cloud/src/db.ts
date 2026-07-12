// D1/SQLite data layer. The same SQL runs on Cloudflare D1 and on the local bun:sqlite shim, so there
// is a single source of truth for the schema and queries regardless of where the worker runs.
//
// Multi-app: every row is scoped by an `app` column. Legacy single-app databases (no `app` column) are
// migrated in place by ensureSchema — existing rows are re-homed under the DEFAULT app.

import type { D1Like } from "./types.ts";

const SCHEMA: string[] = [
  `CREATE TABLE IF NOT EXISTS releases (
     app           TEXT    NOT NULL,
     channel       TEXT    NOT NULL,
     seq           INTEGER NOT NULL,
     release_id    TEXT    NOT NULL,
     created_at    TEXT    NOT NULL,
     manifest_json TEXT    NOT NULL,
     published_at  TEXT    NOT NULL,
     PRIMARY KEY (app, channel, seq)
   )`,
  `CREATE TABLE IF NOT EXISTS channels (
     app         TEXT    NOT NULL,
     channel     TEXT    NOT NULL,
     current_seq INTEGER NOT NULL,
     boot_seq    INTEGER,
     updated_at  TEXT    NOT NULL,
     PRIMARY KEY (app, channel)
   )`,
  // The bootstrapper is its own track: its own seq history + pointer, advanced independently of app releases.
  `CREATE TABLE IF NOT EXISTS bootstrappers (
     app           TEXT    NOT NULL,
     channel       TEXT    NOT NULL,
     seq           INTEGER NOT NULL,
     version       TEXT    NOT NULL,
     created_at    TEXT    NOT NULL,
     manifest_json TEXT    NOT NULL,
     published_at  TEXT    NOT NULL,
     PRIMARY KEY (app, channel, seq)
   )`,
  `CREATE TABLE IF NOT EXISTS config (
     app         TEXT PRIMARY KEY,
     config_json TEXT NOT NULL,
     updated_at  TEXT NOT NULL
   )`,
  `CREATE TABLE IF NOT EXISTS settings (
     key        TEXT PRIMARY KEY,
     value      TEXT NOT NULL,
     updated_at TEXT NOT NULL
   )`,
];

async function tableExists(db: D1Like, name: string): Promise<boolean> {
  const row = await db
    .prepare(`SELECT name FROM sqlite_master WHERE type = 'table' AND name = ?`)
    .bind(name)
    .first<{ name: string }>();
  return row != null;
}

async function hasColumn(db: D1Like, table: string, column: string): Promise<boolean> {
  try {
    await db.prepare(`SELECT ${column} FROM ${table} LIMIT 1`).first();
    return true;
  } catch {
    return false;
  }
}

/**
 * Creates the current schema and migrates a legacy single-app database in place. The legacy → multi-app
 * migration is a staged rename/copy/drop so an interrupted run resumes cleanly:
 *   1. a table that exists WITHOUT an `app` column is renamed to `<t>_legacy`;
 *   2. the current-schema tables are created (IF NOT EXISTS);
 *   3. rows are copied out of any `<t>_legacy` under `defaultApp`, then the legacy table is dropped.
 * A crash between any two steps leaves a state a re-run recognizes and finishes.
 */
export async function ensureSchema(db: D1Like, defaultApp = "argon"): Promise<void> {
  for (const t of ["releases", "channels", "bootstrappers", "config"]) {
    if ((await tableExists(db, t)) && !(await hasColumn(db, t, "app"))) {
      await db.prepare(`ALTER TABLE ${t} RENAME TO ${t}_legacy`).run();
    }
  }

  for (const stmt of SCHEMA) await db.prepare(stmt).run();

  if (await tableExists(db, "releases_legacy")) {
    await db
      .prepare(
        `INSERT OR IGNORE INTO releases (app, channel, seq, release_id, created_at, manifest_json, published_at)
         SELECT ?, channel, seq, release_id, created_at, manifest_json, published_at FROM releases_legacy`,
      )
      .bind(defaultApp)
      .run();
    await db.prepare(`DROP TABLE releases_legacy`).run();
  }

  if (await tableExists(db, "channels_legacy")) {
    // Very old databases may predate the boot_seq column; add it so the copy below always has it.
    try {
      await db.prepare(`ALTER TABLE channels_legacy ADD COLUMN boot_seq INTEGER`).run();
    } catch {
      /* column already exists */
    }
    await db
      .prepare(
        `INSERT OR IGNORE INTO channels (app, channel, current_seq, boot_seq, updated_at)
         SELECT ?, channel, current_seq, boot_seq, updated_at FROM channels_legacy`,
      )
      .bind(defaultApp)
      .run();
    await db.prepare(`DROP TABLE channels_legacy`).run();
  }

  if (await tableExists(db, "bootstrappers_legacy")) {
    await db
      .prepare(
        `INSERT OR IGNORE INTO bootstrappers (app, channel, seq, version, created_at, manifest_json, published_at)
         SELECT ?, channel, seq, version, created_at, manifest_json, published_at FROM bootstrappers_legacy`,
      )
      .bind(defaultApp)
      .run();
    await db.prepare(`DROP TABLE bootstrappers_legacy`).run();
  }

  if (await tableExists(db, "config_legacy")) {
    await db
      .prepare(
        `INSERT OR IGNORE INTO config (app, config_json, updated_at)
         SELECT ?, config_json, updated_at FROM config_legacy WHERE id = 1`,
      )
      .bind(defaultApp)
      .run();
    await db.prepare(`DROP TABLE config_legacy`).run();
  }
}

export interface ReleaseRow {
  app: string;
  channel: string;
  seq: number;
  release_id: string;
  created_at: string;
  manifest_json: string;
  published_at: string;
}

export interface ChannelRow {
  app: string;
  channel: string;
  current_seq: number;
  boot_seq: number | null;
  updated_at: string;
}

/** Every app that has any channel or config — the GC/admin iteration set. */
export async function listApps(db: D1Like): Promise<string[]> {
  const rows = await db
    .prepare(`SELECT app FROM channels UNION SELECT app FROM config ORDER BY app`)
    .all<{ app: string }>();
  return rows.results.map((r) => r.app);
}

export async function maxSeq(db: D1Like, app: string, channel: string): Promise<number> {
  const row = await db
    .prepare(`SELECT MAX(seq) AS m FROM releases WHERE app = ? AND channel = ?`)
    .bind(app, channel)
    .first<{ m: number | null }>();
  return row?.m ?? 0;
}

export async function insertRelease(
  db: D1Like,
  app: string,
  channel: string,
  seq: number,
  releaseId: string,
  createdAt: string,
  manifestJson: string,
): Promise<void> {
  await db
    .prepare(
      `INSERT OR REPLACE INTO releases (app, channel, seq, release_id, created_at, manifest_json, published_at)
       VALUES (?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(app, channel, seq, releaseId, createdAt, manifestJson, new Date().toISOString())
    .run();
}

export async function getRelease(db: D1Like, app: string, channel: string, seq: number): Promise<ReleaseRow | null> {
  return db
    .prepare(`SELECT * FROM releases WHERE app = ? AND channel = ? AND seq = ?`)
    .bind(app, channel, seq)
    .first<ReleaseRow>();
}

export async function deleteRelease(db: D1Like, app: string, channel: string, seq: number): Promise<void> {
  await db.prepare(`DELETE FROM releases WHERE app = ? AND channel = ? AND seq = ?`).bind(app, channel, seq).run();
}

export async function listReleaseSeqs(db: D1Like, app: string, channel: string): Promise<number[]> {
  const rows = await db
    .prepare(`SELECT seq FROM releases WHERE app = ? AND channel = ? ORDER BY seq DESC`)
    .bind(app, channel)
    .all<{ seq: number }>();
  return rows.results.map((r) => r.seq);
}

export async function setPointer(db: D1Like, app: string, channel: string, seq: number): Promise<void> {
  await db
    .prepare(
      `INSERT INTO channels (app, channel, current_seq, updated_at) VALUES (?, ?, ?, ?)
       ON CONFLICT(app, channel) DO UPDATE SET current_seq = excluded.current_seq, updated_at = excluded.updated_at`,
    )
    .bind(app, channel, seq, new Date().toISOString())
    .run();
}

export async function getPointer(db: D1Like, app: string, channel: string): Promise<number | null> {
  const row = await db
    .prepare(`SELECT current_seq FROM channels WHERE app = ? AND channel = ?`)
    .bind(app, channel)
    .first<{ current_seq: number }>();
  return row?.current_seq ?? null;
}

export async function currentRelease(db: D1Like, app: string, channel: string): Promise<ReleaseRow | null> {
  return db
    .prepare(
      `SELECT r.* FROM releases r
       JOIN channels c ON c.app = r.app AND c.channel = r.channel AND c.current_seq = r.seq
       WHERE r.app = ? AND r.channel = ?`,
    )
    .bind(app, channel)
    .first<ReleaseRow>();
}

export async function allChannels(db: D1Like, app: string): Promise<ChannelRow[]> {
  const rows = await db.prepare(`SELECT * FROM channels WHERE app = ?`).bind(app).all<ChannelRow>();
  return rows.results;
}

// ── bootstrapper track (parallel to releases, with its own seq + pointer) ──

export interface BootstrapperRow {
  app: string;
  channel: string;
  seq: number;
  version: string;
  created_at: string;
  manifest_json: string;
  published_at: string;
}

export async function maxBootSeq(db: D1Like, app: string, channel: string): Promise<number> {
  const row = await db
    .prepare(`SELECT MAX(seq) AS m FROM bootstrappers WHERE app = ? AND channel = ?`)
    .bind(app, channel)
    .first<{ m: number | null }>();
  return row?.m ?? 0;
}

export async function insertBootstrapper(
  db: D1Like, app: string, channel: string, seq: number, version: string, createdAt: string, manifestJson: string,
): Promise<void> {
  await db
    .prepare(
      `INSERT OR REPLACE INTO bootstrappers (app, channel, seq, version, created_at, manifest_json, published_at)
       VALUES (?, ?, ?, ?, ?, ?, ?)`,
    )
    .bind(app, channel, seq, version, createdAt, manifestJson, new Date().toISOString())
    .run();
}

export async function getBootstrapper(db: D1Like, app: string, channel: string, seq: number): Promise<BootstrapperRow | null> {
  return db
    .prepare(`SELECT * FROM bootstrappers WHERE app = ? AND channel = ? AND seq = ?`)
    .bind(app, channel, seq)
    .first<BootstrapperRow>();
}

export async function deleteBootstrapper(db: D1Like, app: string, channel: string, seq: number): Promise<void> {
  await db.prepare(`DELETE FROM bootstrappers WHERE app = ? AND channel = ? AND seq = ?`).bind(app, channel, seq).run();
}

export async function listBootSeqs(db: D1Like, app: string, channel: string): Promise<number[]> {
  const rows = await db
    .prepare(`SELECT seq FROM bootstrappers WHERE app = ? AND channel = ? ORDER BY seq DESC`)
    .bind(app, channel)
    .all<{ seq: number }>();
  return rows.results.map((r) => r.seq);
}

export async function setBootPointer(db: D1Like, app: string, channel: string, seq: number): Promise<void> {
  await db
    .prepare(
      `INSERT INTO channels (app, channel, current_seq, boot_seq, updated_at) VALUES (?, ?, 0, ?, ?)
       ON CONFLICT(app, channel) DO UPDATE SET boot_seq = excluded.boot_seq, updated_at = excluded.updated_at`,
    )
    .bind(app, channel, seq, new Date().toISOString())
    .run();
}

export async function getBootPointer(db: D1Like, app: string, channel: string): Promise<number | null> {
  const row = await db
    .prepare(`SELECT boot_seq FROM channels WHERE app = ? AND channel = ?`)
    .bind(app, channel)
    .first<{ boot_seq: number | null }>();
  return row?.boot_seq ?? null;
}

export async function currentBootstrapper(db: D1Like, app: string, channel: string): Promise<BootstrapperRow | null> {
  return db
    .prepare(
      `SELECT b.* FROM bootstrappers b
       JOIN channels c ON c.app = b.app AND c.channel = b.channel AND c.boot_seq = b.seq
       WHERE b.app = ? AND b.channel = ?`,
    )
    .bind(app, channel)
    .first<BootstrapperRow>();
}

export async function getConfig(db: D1Like, app: string): Promise<string | null> {
  const row = await db.prepare(`SELECT config_json FROM config WHERE app = ?`).bind(app).first<{ config_json: string }>();
  return row?.config_json ?? null;
}

export async function setConfig(db: D1Like, app: string, configJson: string): Promise<void> {
  await db
    .prepare(
      `INSERT INTO config (app, config_json, updated_at) VALUES (?, ?, ?)
       ON CONFLICT(app) DO UPDATE SET config_json = excluded.config_json, updated_at = excluded.updated_at`,
    )
    .bind(app, configJson, new Date().toISOString())
    .run();
}

export async function getSetting(db: D1Like, key: string): Promise<string | null> {
  const row = await db.prepare(`SELECT value FROM settings WHERE key = ?`).bind(key).first<{ value: string }>();
  return row?.value ?? null;
}

export async function setSetting(db: D1Like, key: string, value: string): Promise<void> {
  await db
    .prepare(
      `INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?)
       ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at`,
    )
    .bind(key, value, new Date().toISOString())
    .run();
}
