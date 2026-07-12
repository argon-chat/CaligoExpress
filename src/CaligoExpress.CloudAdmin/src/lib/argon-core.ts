// Standalone shim for the bits of @argon/core this app uses (the real package lives in the monorepo).
// Aliased to @argon/core in vite.config.ts / tsconfig paths.

export const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export const logger = {
  info: (...a: unknown[]) => console.info("[caligo]", ...a),
  warn: (...a: unknown[]) => console.warn("[caligo]", ...a),
  error: (...a: unknown[]) => console.error("[caligo]", ...a),
  debug: (...a: unknown[]) => console.debug("[caligo]", ...a),
};
