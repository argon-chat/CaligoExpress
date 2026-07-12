// Multi-app scope for the admin UI. The worker serves the default app (Argon) on the legacy
// un-prefixed routes and every other app under /apps/<app>/...; the request interceptor in api.ts
// rewrites app-scoped paths for the selected app so pages stay app-agnostic.

import { ref } from "vue";
import { api } from "./api";

export interface AppInfo {
  app: string;
  isDefault: boolean;
  hasToken: boolean;
}

const STORAGE_KEY = "caligo.selectedApp";

export const apps = ref<AppInfo[]>([]);
export const defaultApp = ref("argon");
/** The app all app-scoped pages operate on. Always a concrete slug (default app included). */
export const selectedApp = ref(localStorage.getItem(STORAGE_KEY) ?? "");

export function setSelectedApp(app: string): void {
  selectedApp.value = app;
  localStorage.setItem(STORAGE_KEY, app);
}

/** True when requests must be rewritten under /apps/<app>. The default app uses the legacy routes. */
export function isPrefixed(): boolean {
  return !!selectedApp.value && selectedApp.value !== defaultApp.value;
}

export async function loadApps(): Promise<void> {
  const d = (await api.get<{ defaultApp: string; apps: AppInfo[] }>("/admin/apps")).data;
  defaultApp.value = d.defaultApp;
  apps.value = d.apps;
  if (!selectedApp.value || !d.apps.some((a) => a.app === selectedApp.value)) {
    setSelectedApp(d.defaultApp);
  }
}

// Worker-global endpoints that must NEVER be rewritten under /apps/<app> — everything else the UI
// calls is app-scoped (overview, releases, config.json, promote/rollback, per-app settings).
const GLOBAL_PATHS = [
  "/me",
  "/health",
  "/admin/apps",
  "/admin/settings/mirrors",
  "/admin/settings/keep",
  "/admin/settings/gc",
  "/admin/gc",
  "/gc",
];

export function scopeUrl(url: string): string {
  if (!isPrefixed()) return url;
  if (url.startsWith("/apps/")) return url;
  if (GLOBAL_PATHS.some((p) => url === p || url.startsWith(`${p}/`))) return url;
  return `/apps/${selectedApp.value}${url}`;
}
