// Promise-based confirmation for pointer moves and other channel-visible actions.
// Usage: if (await confirm({ title, body, action: "Promote", tone: "live" })) { ... }

import { ref } from "vue";

export interface ConfirmRequest {
  title: string;
  /** Plain-language consequence, e.g. "beta moves from seq 11 to seq 12. Clients update on next launch." */
  body: string;
  /** The verb on the confirming button — same word as the button that opened the dialog. */
  action: string;
  /** live = forward/promote, warn = rollback/caution, bad = destructive. */
  tone: "live" | "warn" | "bad";
  /** Mono details block (app / channel / seq), rendered line by line. */
  details?: string[];
}

export const pending = ref<(ConfirmRequest & { resolve: (ok: boolean) => void }) | null>(null);

export function confirm(req: ConfirmRequest): Promise<boolean> {
  return new Promise((resolve) => {
    pending.value = { ...req, resolve };
  });
}

export function settle(ok: boolean): void {
  pending.value?.resolve(ok);
  pending.value = null;
}
