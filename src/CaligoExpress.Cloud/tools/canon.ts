// Debug: prints the canonical signing bytes (UTF-8) for a manifest file, to compare against the .NET
// JsonCanonicalizer. Used to validate cross-language signature interop.
//
//   bun run tools/canon.ts <manifest.json>

import { canonicalBytesForSigning } from "../src/canonical.ts";

const file = process.argv[2];
if (!file) {
  console.error("usage: bun run tools/canon.ts <manifest.json>");
  process.exit(2);
}

const manifest = JSON.parse(await Bun.file(file).text());
process.stdout.write(new TextDecoder().decode(canonicalBytesForSigning(manifest)));
