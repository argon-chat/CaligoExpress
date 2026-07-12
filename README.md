<img width="1983" height="793" alt="CaligoExpress" src="https://github.com/user-attachments/assets/8d1c3371-6421-420a-9655-5213190358ae" />

# CaligoExpress

Whole-app update and delivery system for desktop applications: Ed25519-signed manifests,
content-defined chunking with cross-version dedup (Steam-style — only changed chunks transfer,
no per-version archives), transactional install with crash recovery and rollback, region-ordered
multi-mirror failover, and a tiny self-updating bootstrapper track that rolls independently of
app releases. One control plane serves **multiple applications**, each with its own channels,
signing keys and CI publish tokens.

## Packages

| package | what | install |
| --- | --- | --- |
| `CaligoExpress` | core engine: manifests, chunking, crypto, update/install/bootstrap flows | `dotnet add package CaligoExpress` |
| `CaligoExpress.Cli` | `caligo` — init/keygen, pack, sign, publish | `dotnet tool install -g CaligoExpress.Cli` |

Published to NuGet from tags (see *Releasing* below).

## What's in this repo

| path | component |
| --- | --- |
| `src/CaligoExpress` | core .NET library (net10.0) |
| `src/CaligoExpress.Cli` | the `caligo` dotnet tool |
| `src/CaligoExpress.Test` | NUnit suite (engine, transactions, end-to-end update flows) |
| `src/CaligoExpress.Cloud` | control-plane worker — Hono on Cloudflare Workers (D1) or local Bun, S3 mirrors, GC |
| `src/CaligoExpress.CloudAdmin` | admin SPA — Vue 3 release console (channels, promote/rollback, settings) |

## How it works

- **Trust chain**: a pinned Ed25519 key → signed `config.json` → signed `release.json` → per-artifact
  SHA-256 → per-chunk hashes. Mirrors are untrusted; releases are signed offline (CI/CLI), and the
  worker verifies signatures against per-app pinned keys before accepting a publish.
- **Channels & pointers**: each channel has a monotonic release sequence and a movable pointer;
  promote/rollback move the pointer among already-published releases. The bootstrapper (launcher)
  is its own signed pointer on the same channel, rolled independently.
- **Multi-app**: one worker + one bucket set serves many apps. The worker's *default app* lives on
  legacy un-prefixed routes and the storage root; every other app gets identical API and layout under
  `/apps/<appId>` and `apps/<appId>/…` keys. Per-app publish tokens can only touch their own app.
- **GC**: a cron sweep keeps N releases per channel (plus everything they reference), prunes
  unreferenced artifacts per app namespace, heals manifest mirrors, and supports a dry-run mode.

## Quickstart (new app)

```bash
dotnet tool install -g CaligoExpress.Cli

# 1. scaffold + keygen: writes caligo.json, keys/k1.key(+.pub), signed config.json
caligo init --config caligo.json --keygen --keys-dir keys \
  --config-out cdn/config.json --worker-url $CALIGO_WORKER --publish-token $TOKEN

# 2. publish a chunked release + the bootstrapper (secrets via env, never argv)
caligo publish --worker $CALIGO_WORKER --channel beta --key-id k1 \
  --name core --core-src out/MyApp-win32-x64 --core-version 1.0.0 --launch-bin app/MyApp.exe
caligo publish --worker $CALIGO_WORKER --channel beta --key-id k1 \
  --boot-bin MyAppSetup.exe --boot-version 1.0.0
```

Env the CLI understands: `CALIGO_WORKER`, `CALIGO_PUBLISH_TOKEN`, `CALIGO_K1_KEY`, `CALIGO_APP_ID`
(multi-app tenant; or `--app`).

## Development

```bash
dotnet test CaligoExpress.slnx                          # library + engine tests

cd src/CaligoExpress.Cloud
bun install && bun test                                  # worker API tests (in-memory D1 shim)
PUBLISH_TOKEN=dev-token bun run dev                      # local control plane on :8787

cd ../CaligoExpress.CloudAdmin
bun install && bun run dev                               # admin SPA on :5173
```

## Releasing

Tag the commit and push — CI tests, packs and publishes both packages to nuget.org:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

Publishing is keyless ([NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)):
the workflow exchanges its GitHub OIDC token for a one-hour API key. One-time setup:

1. nuget.org → profile → **Trusted Publishing** → add a policy: repository owner `argon-chat`,
   repository `CaligoExpress`, workflow file `release.yaml`.
2. Repository secret `NUGET_USER` — the nuget.org profile name that owns the policy/packages.

## License

MIT — see [LICENSE](LICENSE).
