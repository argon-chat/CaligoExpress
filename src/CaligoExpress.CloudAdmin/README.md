# caligo-admin

Admin UI for the CaligoExpress update system — Vue 3 + Vite + Tailwind 4, instrument-panel theme.
Deploys as a static SPA (e.g. Cloudflare Pages); talks to the control-plane worker
(`CaligoExpress.Cloud`).

## What it does

- **Overview** — every channel with its current release, packages and launcher version; GC with a
  dry-run mode and a full in-UI report.
- **Channel** — full release history per channel with **promote / rollback** (moves the channel
  pointer, confirmed with app/channel/seq context). The launcher (bootstrapper) track rolls
  independently.
- **Settings** — the worker's runtime config, stored in D1, split into per-app (trusted signing keys)
  and worker-wide (S3 mirrors, retention, GC mode, per-app publish tokens) zones. Secrets are
  redacted on read; re-enter to rotate.
- **Multi-app** — the sidebar switches between the apps served by one worker; every page and action
  is scoped to the selected app.

## Auth

Sign-in is OIDC + PKCE — see `src/composables/useOAuth.ts`. The access token is sent as a bearer to
the worker, which verifies it (ES256 against your issuer's JWKS) and authorizes any valid token
unless `ADMIN_SCOPE` is configured on the worker. Configure the identity provider at build time:

| env (build-time, baked into the bundle) | meaning |
| --- | --- |
| `VITE_WORKER_URL` | control-plane worker base URL |
| `VITE_OAUTH_AUTHORIZE_URL` | OIDC authorize endpoint |
| `VITE_OAUTH_TOKEN_URL` | OIDC token endpoint |
| `VITE_OAUTH_CLIENT_ID` | public (PKCE) client id; must allow the `<origin>/callback` redirect |

## Develop

```bash
bun install
bun run dev          # http://localhost:5173, expects a worker at VITE_WORKER_URL (default http://localhost:8787)
bun run typecheck
```

Run the worker locally alongside it:

```bash
cd ../CaligoExpress.Cloud && PUBLISH_TOKEN=dev-token bun run dev   # http://localhost:8787
```

The worker allows `http://localhost*` origins for CORS in addition to `ADMIN_ORIGIN`.

## Build & deploy (Cloudflare Pages)

```bash
bun run build                 # -> dist/
bunx wrangler pages deploy    # uses wrangler.toml (name=caligo-admin, pages_build_output_dir=dist)
```

`wrangler.toml` holds the Pages config; `public/_redirects` provides the SPA fallback
(`/* /index.html 200`). All `VITE_*` vars are **build-time** (baked into the bundle) — set them in the
environment that runs `bun run build` (locally via `.env`, or the Pages project's build env).

## Notes

- `@argon/core` resolves to a local shim (`src/lib/argon-core.ts`) for standalone builds.
- Worker secrets (S3 creds, mirrors, retention, pinned keys) are bootstrapped **from this UI** into D1,
  so the worker does not need them set as Cloudflare secrets up front.
