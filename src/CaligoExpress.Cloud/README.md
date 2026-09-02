# caligo-cloud

Control plane for the CaligoExpress update system. It is the source of truth for **which release each
channel currently points at**, records published releases (with monotonic sequence + dependency
validation), and keeps the signed manifests replicated across the S3 mirrors. Artifacts (zips, deltas,
package manifests) are served directly from S3/CDN; this worker only manages pointers, signing-trust
checks, replication, and garbage collection.

## Multi-app

One worker serves **many apps** sharing the same D1, mirrors, buckets and hosts:

- The **default app** (`DEFAULT_APP`, default `argon`) lives on the legacy un-prefixed routes
  (`/config.json`, `/:channel/release.json`, …) and the ROOT storage layout — shipped Argon
  bootstrappers hard-code those paths, so this layout is frozen.
- Every **other app** gets the identical API under `/apps/:app/...` and the identical storage layout
  under the `apps/<app>/` key prefix. Publishes must carry `app` inside the signed manifest and may
  only reference keys inside their own prefix (enforced on `/publish` and `/admin/presign`).
- **Per-app state:** channels/releases/bootstrappers/config rows (D1 `app` column), pinned public
  keys, and optional per-app publish tokens (Settings → App publish tokens; each token works only on
  its own app's routes). **Global state:** mirrors, retention, the master `PUBLISH_TOKEN`, SSO.
- **Signatures are mandatory per app:** a publish to an app with no pinned keys is a 403 telling you
  to pin the key first (never a silent skip), and a manifest signed with an unknown `keyId` is a 403
  naming the app's actual key ids. Dev workers can opt out with `ALLOW_UNSIGNED=1`.
- **GC** partitions the shared bucket by prefix: each app's keep set only prunes its own namespace,
  keys under `apps/<x>/` for an unknown `x` are never touched, and a `gcDryRun` setting (or
  `POST /gc {"dryRun":true}`) reports would-be deletions without deleting — use it when rolling out
  layout changes.
- A legacy single-app database is migrated in place on first boot (rows re-homed under `DEFAULT_APP`);
  the old routes keep serving byte-identical responses.

Runs **locally on Bun** and **on Cloudflare Workers** from the same code:

- Database: **D1** on Cloudflare; a `bun:sqlite` shim with the identical SQL for local dev.
- Storage: **`Bun.S3Client`** (native, works in both runtimes) for the S3 mirrors.
- Routing: **Hono**.
- Scheduled GC + mirror self-heal via the Cloudflare **cron trigger** (or a local interval).

## Run locally

```bash
bun install
PUBLISH_TOKEN=dev-token bun run dev     # http://localhost:8787, sqlite at ./caligo-cloud.sqlite
bun test                                # API tests (in-memory D1 shim)
```

Env vars (all optional except where noted):

| var               | meaning                                                        |
|-------------------|---------------------------------------------------------------|
| `PUBLISH_TOKEN`   | bearer token guarding mutating endpoints (default `dev-token`)|
| `MIRRORS`         | JSON array of S3 mirror configs (default `[]` = no mirrors)   |
| `PINNED_PUBKEYS`  | JSON `{keyId: base64PublicKey}` for release signature checks  |
| `DB_PATH`         | local sqlite path                                             |
| `PORT`            | local port (default 8787)                                     |
| `KEEP_PER_CHANNEL`| releases retained per channel for rollback (default 5)        |
| `GC_INTERVAL_MS`  | if set, runs GC on a local interval (mimics the CF cron)      |

`MIRRORS` entry shape:

```json
[{ "name": "r2-primary", "endpoint": "https://<acct>.r2.cloudflarestorage.com",
   "bucket": "argon-cdn", "region": "auto",
   "accessKeyId": "...", "secretAccessKey": "...",
   "publicBaseUrl": "https://updates.example.com" }]
```

## Deploy to Cloudflare

```bash
wrangler d1 create caligo                       # paste database_id into wrangler.toml
wrangler d1 migrations apply caligo --remote
wrangler secret put PUBLISH_TOKEN
wrangler secret put MIRRORS
wrangler secret put PINNED_PUBKEYS              # optional but recommended
wrangler deploy
```

## API

| method | path                     | auth | purpose |
|--------|--------------------------|------|---------|
| GET    | `/config.json`           | no   | serve the signed bootstrap config |
| GET    | `/:channel/release.json` | no   | serve the channel's current signed release |
| POST   | `/publish`               | yes  | record a signed release, advance the channel pointer, replicate |
| POST   | `/promote`               | yes  | move a channel pointer forward to an existing seq `{channel, seq}` |
| POST   | `/rollback`              | yes  | move a channel pointer back to an existing seq `{channel, seq}` |
| POST   | `/config`                | yes  | store + replicate the signed config (raw JSON body) |
| POST   | `/gc`                    | yes  | run GC + mirror self-heal now |

Releases are **signed offline** by the `caligo` CLI (Ed25519, private key in CI). The worker verifies
that signature against `PINNED_PUBKEYS` before accepting a publish — the TypeScript canonicalizer is
byte-compatible with the .NET one (validated end to end). On publish it checks the release sequence is
strictly monotonic per channel and the dependency graph is acyclic and self-contained, then replicates
`/:channel/release.json` to every mirror.

Cross-channel promotion (e.g. beta → live) is a CLI re-publish to the `live` channel (re-signed),
because the channel is part of the signed manifest. `/promote` and `/rollback` move a channel's pointer
among releases already published **to that channel** and need no re-signing.

## Garbage collection

The cron trigger (and `POST /gc`) keeps, per channel, the current release plus the last
`KEEP_PER_CHANNEL - 1` releases and everything they reference, re-uploads the current manifests to
every mirror (self-healing drift), and deletes unreferenced artifacts (old `*.delta`, superseded
`*.zip` / `package.json`, stale bootstrapper binaries). It never deletes `release.json` or
`config.json`.

## Tools

```bash
bun run tools/verify-release.ts <release.json> <pubkey.base64> [keyId]   # verify a signed release
bun run tools/canon.ts <manifest.json>                                   # print canonical signing bytes
```
