// S3 mirror client built on `s3mini` — a tiny SigV4-over-fetch S3 client that runs on the Cloudflare
// Workers (workerd) runtime as well as locally, so one implementation covers every environment.
// (Bun.S3Client only exists in the Bun runtime, which the deployed worker does not use.)

import { S3mini } from "s3mini";

export interface MirrorConfig {
  name: string;
  endpoint: string; // host, e.g. https://<acct>.r2.cloudflarestorage.com (bucket appended internally)
  bucket: string;
  region: string; // "auto" for R2
  accessKeyId: string;
  secretAccessKey: string;
  publicBaseUrl?: string; // host clients actually fetch from (reference only)
}

export class S3Mirror {
  private readonly s3: S3mini;

  constructor(public readonly cfg: MirrorConfig) {
    const base = cfg.endpoint.replace(/\/+$/, "");
    this.s3 = new S3mini({
      accessKeyId: cfg.accessKeyId,
      secretAccessKey: cfg.secretAccessKey,
      // s3mini's endpoint includes the bucket.
      endpoint: `${base}/${cfg.bucket}`,
      region: cfg.region || "auto",
    });
  }

  async put(key: string, body: string | Uint8Array | ArrayBuffer, contentType = "application/octet-stream"): Promise<void> {
    const res = await this.s3.putObject(key, body, contentType);
    if (!res.ok) throw new Error(`S3 PUT ${this.cfg.name}/${key} -> ${res.status}`);
  }

  async delete(key: string): Promise<void> {
    await this.s3.deleteObject(key);
  }

  async exists(key: string): Promise<boolean> {
    return (await this.s3.objectExists(key)) === true;
  }

  /** Fetch an object's text (used by GC to read a package.json and expand its chunk references). */
  async get(key: string): Promise<string | null> {
    return await this.s3.getObject(key);
  }

  async list(prefix = ""): Promise<string[]> {
    // s3mini requires a non-empty delimiter, but it only uses "/" to mean "bucket root" — the value is NOT
    // sent to S3 as a delimiter on the fetch path, so the listing comes back flat (every key in Contents),
    // paginated internally. That's exactly the recursive listing GC needs. (Any OTHER delimiter value gets
    // put into the request PATH instead and returns nothing — which is why the GC saw `listed 0`.)
    const objects = await this.s3.listObjects("/", prefix);
    return (objects ?? []).map((o) => o.Key).filter((k): k is string => typeof k === "string");
  }

  /** A short-lived presigned PUT URL so a publisher can upload directly to S3 without holding creds. */
  presignPut(key: string, expiresIn = 900): Promise<string> {
    return this.s3.getPresignedUrl("PUT", key, expiresIn);
  }
}

export function parseMirrors(json?: string): MirrorConfig[] {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? (parsed as MirrorConfig[]) : [];
  } catch {
    return [];
  }
}

export function buildMirrors(json?: string): S3Mirror[] {
  return parseMirrors(json).map((cfg) => new S3Mirror(cfg));
}
