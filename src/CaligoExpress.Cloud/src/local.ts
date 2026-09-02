// Local Bun entry. Uses a bun:sqlite-backed D1 shim so the exact worker code runs without Cloudflare,
// and Bun.serve for HTTP. S3 access is identical to production (Bun.S3Client). Configure via env:
//   PUBLISH_TOKEN, MIRRORS (JSON), PINNED_PUBKEYS (JSON), DB_PATH, PORT, KEEP_PER_CHANNEL, GC_INTERVAL_MS

import { createApp } from "./app.ts";
import { ensureSchema } from "./db.ts";
import { openBunD1 } from "./db-bun.ts";
import { runGc } from "./gc.ts";
import { defaultAppOf, type Env } from "./types.ts";

const dbPath = process.env.DB_PATH ?? "./caligo-cloud.sqlite";
const database = openBunD1(dbPath);

const env: Env = {
  DB: database,
  PUBLISH_TOKEN: process.env.PUBLISH_TOKEN ?? "dev-token",
  PINNED_PUBKEYS: process.env.PINNED_PUBKEYS,
  MIRRORS: process.env.MIRRORS ?? "[]",
  KEEP_PER_CHANNEL: process.env.KEEP_PER_CHANNEL,
  DEFAULT_APP: process.env.DEFAULT_APP,
  // Local dev worker: unsigned publishes allowed by default (production requires pinned keys).
  ALLOW_UNSIGNED: process.env.ALLOW_UNSIGNED ?? "1",
};

await ensureSchema(database, defaultAppOf(env));

const app = createApp();
const port = Number(process.env.PORT ?? "8787");

Bun.serve({ port, fetch: (req) => app.fetch(req, env) });
console.log(`caligo-cloud listening on http://localhost:${port} (db: ${dbPath})`);

// Mimic the Cloudflare cron locally when asked.
if (process.env.GC_INTERVAL_MS) {
  const ms = Number(process.env.GC_INTERVAL_MS);
  setInterval(() => void runGc(env).catch((e) => console.error("gc error:", e)), ms);
}
