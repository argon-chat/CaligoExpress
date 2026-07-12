// Minimal SemVer-ish precedence comparison (build metadata ignored). All numeric core components are
// compared, not just the first three, so GitVersion's 4-part CI versions (e.g. 2.16.1.221 vs 2.16.1.222)
// order correctly for the worker's downgrade/bump checks.

export function compareSemver(a: string, b: string): number {
  const [ca, pa] = split(a);
  const [cb, pb] = split(b);

  const na = ca.split(".").map((n) => Number(n) || 0);
  const nb = cb.split(".").map((n) => Number(n) || 0);
  const len = Math.max(na.length, nb.length);
  for (let i = 0; i < len; i++) {
    const d = (na[i] ?? 0) - (nb[i] ?? 0);
    if (d !== 0) return Math.sign(d);
  }

  // A version with a pre-release has lower precedence than one without.
  if (!pa && !pb) return 0;
  if (!pa) return 1;
  if (!pb) return -1;
  return comparePre(pa, pb);
}

function split(v: string): [string, string] {
  const noMeta = (v ?? "").trim().replace(/^v/i, "").split("+")[0] ?? "";
  const i = noMeta.indexOf("-");
  return i < 0 ? [noMeta, ""] : [noMeta.slice(0, i), noMeta.slice(i + 1)];
}

function comparePre(a: string, b: string): number {
  const aa = a.split(".");
  const bb = b.split(".");
  const n = Math.max(aa.length, bb.length);
  for (let i = 0; i < n; i++) {
    const x = aa[i];
    const y = bb[i];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    const xn = /^\d+$/.test(x);
    const yn = /^\d+$/.test(y);
    if (xn && yn) {
      const d = Number(x) - Number(y);
      if (d !== 0) return Math.sign(d);
    } else if (xn) {
      return -1;
    } else if (yn) {
      return 1;
    } else if (x !== y) {
      return x < y ? -1 : 1;
    }
  }
  return 0;
}
