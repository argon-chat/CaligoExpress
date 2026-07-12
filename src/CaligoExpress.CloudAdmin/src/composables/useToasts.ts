// Action feedback as transient toasts (bottom-right). One global queue; rendered by Toasts.vue.

import { ref } from "vue";

export interface Toast {
  id: number;
  kind: "ok" | "err";
  text: string;
}

let nextId = 1;
export const toasts = ref<Toast[]>([]);

function push(kind: Toast["kind"], text: string, ttlMs: number): void {
  const id = nextId++;
  toasts.value.push({ id, kind, text });
  setTimeout(() => {
    toasts.value = toasts.value.filter((t) => t.id !== id);
  }, ttlMs);
}

/** Confirmation that an action completed, in the action's own words ("Promoted to seq 12"). */
export const toastOk = (text: string): void => push("ok", text, 4000);

/** What went wrong — errors stay longer so they can be read. */
export const toastErr = (text: string): void => push("err", text, 8000);
