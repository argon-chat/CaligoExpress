import type { Context, Next } from "hono";
import { getAppTokens } from "./settings.ts";
import { AegisDefaults, defaultAppOf, type Env, type Vars } from "./types.ts";
import { hasScope, verifyJwt } from "./jwt.ts";

type Ctx = Context<{ Bindings: Env; Variables: Vars }>;

/** The app a request is scoped to: the `:app` route param, or the default app on legacy routes. */
export function routeApp(c: Ctx): string {
  return c.req.param("app") ?? defaultAppOf(c.env);
}

/** True for a per-app CI identity (`ci:<app>`) — these must never touch global/admin-sensitive state. */
export function isAppCi(user: { sub?: string }): boolean {
  return typeof user.sub === "string" && user.sub.startsWith("ci:");
}

/**
 * Guards mutating/admin endpoints. Accepts, in order:
 *  - the static PUBLISH_TOKEN (master machine token — full access across all apps),
 *  - a per-app publish token (settings `appTokens`) — valid ONLY on the route's own app,
 *  - a valid Aegis SSO access token (ES256).
 *
 * Operator access is gated by the OAuth client itself, so by default any valid token is authorized.
 * Scope enforcement is opt-in: set ADMIN_SCOPE to a non-empty value to additionally require that scope
 * (checked against the token's `scope`, `scopes[]` or `roles[]`). The identity is stashed as `user`.
 */
export function requireAuth() {
  return async (c: Ctx, next: Next): Promise<Response | void> => {
    const header = c.req.header("authorization") ?? "";
    const token = header.startsWith("Bearer ") ? header.slice(7) : "";
    if (!token) return c.json({ error: "unauthorized" }, 401);

    // 1) Static publish token (machine clients) — always full access.
    if (c.env.PUBLISH_TOKEN && timingSafeEqual(token, c.env.PUBLISH_TOKEN)) {
      c.set("user", { sub: "ci", scope: AegisDefaults.adminScope });
      return next();
    }

    // 2) Per-app publish token — machine access scoped to the route's app only.
    const appToken = (await getAppTokens(c.env))[routeApp(c)];
    if (appToken && timingSafeEqual(token, appToken)) {
      c.set("user", { sub: `ci:${routeApp(c)}`, scope: AegisDefaults.adminScope });
      return next();
    }

    // 3) SSO JWT — valid token is enough unless ADMIN_SCOPE is configured.
    try {
      const claims = await verifyJwt(token, {
        issuer: c.env.AEGIS_ISSUER ?? AegisDefaults.issuer,
        jwksUri: c.env.AEGIS_JWKS ?? AegisDefaults.jwks,
      });
      const required = c.env.ADMIN_SCOPE?.trim();
      if (required && !hasScope(claims, required)) {
        return c.json({ error: "forbidden", requiredScope: required }, 403);
      }
      c.set("user", claims);
      return next();
    } catch {
      return c.json({ error: "unauthorized" }, 401);
    }
  };
}

/**
 * Restricts to machine tokens only: the master PUBLISH_TOKEN, or the route app's own publish token.
 * Used for endpoints that return raw signing material (presigned S3 upload URLs) — SSO operators get
 * the redacted admin view instead, never raw signing capability.
 */
export function requireMachine() {
  return async (c: Ctx, next: Next): Promise<Response | void> => {
    const header = c.req.header("authorization") ?? "";
    const token = header.startsWith("Bearer ") ? header.slice(7) : "";
    if (token) {
      if (c.env.PUBLISH_TOKEN && timingSafeEqual(token, c.env.PUBLISH_TOKEN)) {
        c.set("user", { sub: "ci", scope: AegisDefaults.adminScope });
        return next();
      }
      const appToken = (await getAppTokens(c.env))[routeApp(c)];
      if (appToken && timingSafeEqual(token, appToken)) {
        c.set("user", { sub: `ci:${routeApp(c)}`, scope: AegisDefaults.adminScope });
        return next();
      }
    }
    return c.json({ error: "machine token required" }, 403);
  };
}

function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
