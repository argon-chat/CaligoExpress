// Server-side validation the worker enforces on publish: monotonic release sequence and an acyclic,
// self-contained dependency graph. The .NET planner also topo-sorts deps, but the control plane is the
// right place to reject a malformed release before it can ever reach a client.

import type { PackageRef, ReleaseManifest } from "./types.ts";
import { compareSemver } from "./semver.ts";

export class ValidationError extends Error {}

export function assertMonotonic(currentMaxSeq: number, newSeq: number): void {
  if (newSeq <= currentMaxSeq) {
    throw new ValidationError(
      `release sequence ${newSeq} must be greater than the current max ${currentMaxSeq}`,
    );
  }
}

/**
 * Guards against publishing an older or unchanged set of versions over the latest release of a channel:
 * no package or the bootstrapper may go backwards, and (unless forced) at least one version must bump.
 */
export function assertVersionProgress(prev: ReleaseManifest, next: ReleaseManifest, requireBump = true): void {
  let bumped = false;

  const prevByName = new Map(prev.packages.map((p) => [p.name, p.version]));
  for (const p of next.packages) {
    const prevVersion = prevByName.get(p.name);
    if (prevVersion === undefined) {
      bumped = true; // a newly added package counts as progress
      continue;
    }
    const c = compareSemver(p.version, prevVersion);
    if (c < 0) {
      throw new ValidationError(`package '${p.name}' downgrade: ${p.version} < current ${prevVersion}`);
    }
    if (c > 0) bumped = true;
  }

  // The macOS whole-app payload isn't a package, so a mac-only publish (Windows packages unchanged) bumps
  // only macApp.version — count that as progress, else routine mac releases would be rejected as "no bump".
  const prevMac = prev.macApp?.version;
  const nextMac = next.macApp?.version;
  if (nextMac !== undefined) {
    if (prevMac === undefined) {
      bumped = true; // first mac payload on this channel
    } else {
      const cm = compareSemver(nextMac, prevMac);
      if (cm < 0) throw new ValidationError(`macApp downgrade: ${nextMac} < current ${prevMac}`);
      if (cm > 0) bumped = true;
    }
  }

  if (requireBump && !bumped) {
    throw new ValidationError("no version bump — every version equals the current release (pass force to override)");
  }
}

/** The bootstrapper track must not go backwards in version (its seq monotonicity is checked separately). */
export function assertBootstrapperProgress(prevVersion: string | null, nextVersion: string): void {
  if (prevVersion !== null && compareSemver(nextVersion, prevVersion) < 0) {
    throw new ValidationError(`bootstrapper downgrade: ${nextVersion} < current ${prevVersion}`);
  }
}

export function assertAcyclicDeps(packages: PackageRef[]): void {
  const names = new Set(packages.map((p) => p.name));
  const depsByName = new Map(packages.map((p) => [p.name, p.deps]));

  // Missing dependency.
  for (const p of packages) {
    for (const dep of p.deps) {
      if (!names.has(dep)) {
        throw new ValidationError(`package '${p.name}' depends on unknown package '${dep}'`);
      }
    }
  }

  // Cycle detection (DFS with a visiting/done coloring).
  const state = new Map<string, number>(); // 1 = visiting, 2 = done
  const visit = (name: string): void => {
    const s = state.get(name);
    if (s === 2) return;
    if (s === 1) throw new ValidationError(`dependency cycle detected at package '${name}'`);
    state.set(name, 1);
    for (const dep of depsByName.get(name) ?? []) visit(dep);
    state.set(name, 2);
  };
  for (const p of packages) visit(p.name);
}
