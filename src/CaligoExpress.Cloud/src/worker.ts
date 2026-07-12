/// <reference types="@cloudflare/workers-types" />
// Cloudflare Workers entry. The DB binding is D1; everything else (S3 via Bun.S3Client, routing via
// Hono) is shared with the local entry. The cron trigger drives garbage collection + mirror self-heal.

import { createApp } from "./app.ts";
import { ensureSchema } from "./db.ts";
import { runGc } from "./gc.ts";
import { defaultAppOf, type Env } from "./types.ts";

const app = createApp();

export default {
  async fetch(req: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    await ensureSchema(env.DB, defaultAppOf(env));
    return app.fetch(req, env, ctx);
  },

  async scheduled(_event: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    await ensureSchema(env.DB, defaultAppOf(env));
    // The cron respects the `gcDryRun` setting (staged-rollout safety switch); log the report so a
    // dry-run's would-delete set is inspectable from the worker logs.
    ctx.waitUntil(runGc(env).then((r) => console.log("gc report", JSON.stringify(r))));
  },
};
