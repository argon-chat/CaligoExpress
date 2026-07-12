// Small shared UI helpers: consistent time display and API error extraction.

/** "3m ago" / "2d ago" — relative time for lists; pair with an exact `title` for hover. */
export function timeAgo(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return iso;
  const s = Math.max(0, (Date.now() - t) / 1000);
  if (s < 60) return "just now";
  const m = s / 60;
  if (m < 60) return `${Math.floor(m)}m ago`;
  const h = m / 60;
  if (h < 24) return `${Math.floor(h)}h ago`;
  const d = h / 24;
  if (d < 30) return `${Math.floor(d)}d ago`;
  return new Date(t).toLocaleDateString();
}

export function exactTime(iso: string): string {
  const t = new Date(iso);
  return Number.isNaN(t.getTime()) ? iso : t.toLocaleString();
}

/** Channel identity hue: the three canonical channels each own a color; anything else stays neutral. */
export function channelTone(channel: string): string {
  if (channel === "live") return "var(--color-live)";
  if (channel === "beta") return "var(--color-info)";
  if (channel === "canary") return "var(--color-warn)";
  return "var(--color-ink-faint)";
}

/** The worker's error body, or a readable fallback. */
export function errMsg(e: unknown, fallback = "Request failed"): string {
  const any = e as { response?: { data?: { error?: string } }; message?: string };
  return any?.response?.data?.error ?? any?.message ?? fallback;
}
