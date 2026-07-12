// Verifies a signed release.json (produced by the .NET `caligo release` CLI) against an Ed25519 public
// key, exercising the TS canonicalizer/verifier. Proves cross-language signature interop.
//
//   bun run tools/verify-release.ts <release.json> <pubkey.base64> [keyId]

import { verifySignedManifest } from "../src/canonical.ts";

const releaseFile = process.argv[2];
const pubFile = process.argv[3];
const keyId = process.argv[4] ?? "k1";

if (!releaseFile || !pubFile) {
  console.error("usage: bun run tools/verify-release.ts <release.json> <pubkey.base64> [keyId]");
  process.exit(2);
}

const release = JSON.parse(await Bun.file(releaseFile).text());
const pub = (await Bun.file(pubFile).text()).trim();

const ok = await verifySignedManifest(release, { [keyId]: pub });
console.log(ok ? "VERIFIED" : "FAILED");
process.exit(ok ? 0 : 1);
