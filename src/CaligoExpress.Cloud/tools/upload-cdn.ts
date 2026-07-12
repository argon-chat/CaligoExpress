// Uploads every file under a local directory to the S3 mirrors.
//
// Preferred: ask the worker for short-lived **presigned PUT URLs** and upload the bytes directly to each
// mirror — so S3 credentials never leave the worker (configured once in the admin). Pass --worker/--token.
// Fallback for CI: set MIRRORS (JSON array of S3 configs) and upload directly with s3mini.
//
//   bun run tools/upload-cdn.ts <dir> --worker https://caligo-worker.example.com --token <PUBLISH_TOKEN> [--kind content|manifest]
//   MIRRORS='[...]' bun run tools/upload-cdn.ts <dir>

import { S3Mirror, parseMirrors } from "../src/s3.ts";

const args = process.argv.slice(2);
const dir = args[0];
const opt = (name: string): string | undefined => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};

const worker = opt("--worker");
const token = opt("--token");
const kind = opt("--kind") ?? "content";

if (!dir) {
  console.error("usage: bun run tools/upload-cdn.ts <dir> --worker <url> --token <tok> [--kind content|manifest]");
  process.exit(2);
}

// Collect the files to upload (relative key -> bytes).
const files: { key: string; bytes: Uint8Array }[] = [];
for await (const rel of new Bun.Glob("**/*").scan({ cwd: dir, onlyFiles: true })) {
  files.push({ key: rel.replaceAll("\\", "/"), bytes: await Bun.file(`${dir}/${rel}`).bytes() });
}
if (files.length === 0) {
  console.log("nothing to upload");
  process.exit(0);
}

if (worker) {
  // ── Presigned path: the worker signs URLs, we PUT bytes directly to each mirror. ──
  const res = await fetch(`${worker.replace(/\/+$/, "")}/admin/presign`, {
    method: "POST",
    headers: { authorization: `Bearer ${token ?? ""}`, "content-type": "application/json" },
    body: JSON.stringify({ kind, keys: files.map((f) => f.key) }),
  });
  if (!res.ok) throw new Error(`presign -> ${res.status} ${await res.text()}`);
  const { items } = (await res.json()) as { items: { key: string; targets: { mirror: string; url: string }[] }[] };
  const byKey = new Map(items.map((i) => [i.key, i.targets]));

  let n = 0;
  for (const f of files) {
    const targets = byKey.get(f.key) ?? [];
    if (targets.length === 0) throw new Error(`no mirrors returned for ${f.key} (configure ${kind} mirrors in the admin)`);
    for (const t of targets) {
      const put = await fetch(t.url, { method: "PUT", body: f.bytes });
      if (!put.ok) throw new Error(`PUT ${t.mirror}/${f.key} -> ${put.status}`);
    }
    n++;
    console.log(`  ${f.key} -> ${targets.map((t) => t.mirror).join(", ")}`);
  }
  console.log(`uploaded ${n} files to ${items[0]?.targets.length ?? 0} mirror(s) [${kind}] via presigned URLs`);
} else {
  // ── Fallback: direct upload with credentials from MIRRORS env. ──
  const mirrors = parseMirrors(process.env.MIRRORS).map((c) => new S3Mirror(c));
  if (mirrors.length === 0) {
    console.error("no --worker and no MIRRORS env — nothing to upload to");
    process.exit(2);
  }
  const contentType = (k: string) => (k.endsWith(".json") ? "application/json" : k.endsWith(".zip") ? "application/zip" : "application/octet-stream");
  for (const f of files) {
    for (const m of mirrors) await m.put(f.key, f.bytes, contentType(f.key));
    console.log(`  ${f.key} -> ${mirrors.map((m) => m.cfg.name).join(", ")}`);
  }
  console.log(`uploaded ${files.length} files to ${mirrors.length} mirror(s) [direct]`);
}
