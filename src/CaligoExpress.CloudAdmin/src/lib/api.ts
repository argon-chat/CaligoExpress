import axios from "axios";
import { ensureValidToken } from "@/composables/useOAuth";

// The control-plane worker. Set VITE_WORKER_URL at build time; defaults to a local dev worker.
const baseURL = import.meta.env.VITE_WORKER_URL ?? "http://localhost:8787";

export const api = axios.create({ baseURL });

// Attach the SSO bearer token (refreshing it first if needed) to every request, and rewrite
// app-scoped paths under /apps/<app> for the selected non-default app (see lib/app-scope.ts).
api.interceptors.request.use(async (config) => {
  const token = await ensureValidToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  if (config.url) {
    const { scopeUrl } = await import("./app-scope"); // dynamic: avoids a circular import at init
    config.url = scopeUrl(config.url);
  }
  return config;
});
