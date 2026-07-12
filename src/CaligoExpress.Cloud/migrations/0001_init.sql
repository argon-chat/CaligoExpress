-- Control-plane schema. Applied on Cloudflare with `wrangler d1 migrations apply caligo`.
-- The worker also runs these idempotently at startup (ensureSchema), so local Bun needs no migration step.

CREATE TABLE IF NOT EXISTS releases (
  channel       TEXT    NOT NULL,
  seq           INTEGER NOT NULL,
  release_id    TEXT    NOT NULL,
  created_at    TEXT    NOT NULL,
  manifest_json TEXT    NOT NULL,
  published_at  TEXT    NOT NULL,
  PRIMARY KEY (channel, seq)
);

CREATE TABLE IF NOT EXISTS channels (
  channel     TEXT PRIMARY KEY,
  current_seq INTEGER NOT NULL,
  updated_at  TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS config (
  id          INTEGER PRIMARY KEY CHECK (id = 1),
  config_json TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);

-- Runtime settings/secrets managed from the admin UI (mirrors with S3 creds, keepPerChannel, pinnedKeys).
CREATE TABLE IF NOT EXISTS settings (
  key        TEXT PRIMARY KEY,
  value      TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
