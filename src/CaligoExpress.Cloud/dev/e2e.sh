#!/usr/bin/env bash
# End to end: the .NET CLI publishes to a local worker backed by a local S3 (SeaweedFS, dev/compose.yaml),
# and the Rust client installs and updates from it. Needs docker, bun, dotnet and cargo.
#
#   src/CaligoExpress.Cloud/dev/e2e.sh          (KEEP=1 leaves the S3 container and the work dir)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
cloud="$(dirname "$here")"
repo="$(cd "$cloud/../.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/caligo-e2e.XXXXXX")"
port="${PORT:-8787}"
worker_pid=""

cleanup() {
  [[ -n "$worker_pid" ]] && kill "$worker_pid" 2>/dev/null || true
  if [[ "${KEEP:-}" != 1 ]]; then
    docker compose -f "$here/compose.yaml" down -v >/dev/null 2>&1 || true
    rm -rf "$work"
  else
    echo "kept $work"
  fi
}
trap cleanup EXIT

step() { printf '\n== %s\n' "$*"; }

step "S3 (SeaweedFS)"
docker compose -f "$here/compose.yaml" up -d --wait s3
docker compose -f "$here/compose.yaml" run --rm bucket

step "worker on :$port"
if curl -sf "http://localhost:$port/health" >/dev/null; then
  echo "something already listens on :$port; stop it or set PORT" >&2
  exit 1
fi
(cd "$cloud" && bun install --frozen-lockfile >/dev/null)
(cd "$cloud" && MIRRORS="$(cat "$here/mirrors.json")" PUBLISH_TOKEN=dev-token DB_PATH="$work/worker.sqlite" PORT="$port" \
  exec bun run src/local.ts) &
worker_pid=$!
for _ in $(seq 50); do curl -sf "http://localhost:$port/health" >/dev/null && break; sleep 0.2; done

step "CLI"
dotnet build "$repo/src/CaligoExpress.Cli" -c Release -v q -nologo >/dev/null
cli() { dotnet "$repo/src/CaligoExpress.Cli/bin/Release/net10.0/CaligoExpress.Cli.dll" "$@"; }

cd "$work"
printf '%s\n' \
  '{ "appName": "E2E", "bootstrapperFileName": "Setup.exe", "defaultChannel": "live",' \
  "  \"seedHosts\": [\"localhost:$port\"], \"scheme\": \"http\", \"keyId\": \"k1\"," \
  '  "mirrors": [{ "host": "localhost:8333/caligo", "regions": ["*"], "priority": 0, "scheme": "http" }],' \
  '  "launch": { "bin": "app/App.exe", "args": [] } }' > caligo.json
cli init --config caligo.json --keygen --keys-dir keys --worker-url "http://localhost:$port" --publish-token dev-token

publish() {
  cli publish --worker "http://localhost:$port" --token dev-token --channel live --key keys/k1.key --key-id k1 \
    --launch-bin app/App.exe "$@" | grep -E 'chunks:|DONE'
}

# v1; v2 patches the big file, drops one small file and adds another; v3 only edits a small file.
mkdir -p v1/res
head -c 12000000 /dev/urandom > v1/App.exe
echo alpha > v1/res/a.txt
echo old > v1/res/old.txt
cp -r v1 v2
rm v2/res/old.txt
echo new > v2/res/new.txt
printf 'PATCHED' | dd of=v2/App.exe bs=1 seek=6000000 conv=notrunc 2>/dev/null
cp -r v2 v3
echo newer > v3/res/new.txt
head -c 300000 /dev/urandom > boot-1.bin
head -c 300000 /dev/urandom > boot-2.bin

export CALIGO_E2E_SEED="localhost:$port" CALIGO_E2E_SCHEME=http CALIGO_E2E_KEY="$(cat keys/k1.pub)" CALIGO_E2E_ROOT="$work/root"
client() {
  (cd "$repo/rust/caligo" && CALIGO_E2E_SRC="$work/$1" CALIGO_E2E_VERSION="$2" CALIGO_E2E_MAX_CHUNKS="$3" \
    cargo test --test e2e -- --ignored --nocapture 2>&1 | grep -E 'chunks downloaded|test result|panicked|error' )
}

# How many chunks the random big file cuts into varies, so only the deterministic counts are checked.
step "v1: fresh install"
publish --core-src v1 --core-version 1.0.0 --boot-bin boot-1.bin --boot-version 1.0.0
client v1 1.0.0 ""

step "v2: update"
publish --core-src v2 --core-version 2.0.0 --boot-bin boot-2.bin --boot-version 1.1.0
client v2 2.0.0 ""

step "v2 again: nothing to download"
client v2 2.0.0 0

step "v3: one small file changed, one chunk"
publish --core-src v3 --core-version 3.0.0
client v3 3.0.0 1

step "ok"
