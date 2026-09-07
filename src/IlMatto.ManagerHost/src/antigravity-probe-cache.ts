import path from "node:path";
import { probeAntigravity, type AntigravityProbe } from "./antigravity.js";

type ProbeEntry = {
  promise: Promise<AntigravityProbe>;
  result?: AntigravityProbe;
};

// Model discovery is a process-wide concern.  A ManagerHost can own many
// visible conversations, but they all use the same Antigravity installation.
// Keeping the promise here prevents every conversation from starting its own
// `agy models` process when it is first selected.
const probeEntries = new Map<string, ProbeEntry>();

function executableKey(executable: string): string {
  const value = executable.trim();
  if (!value) return "agy";
  // Preserve bare PATH commands while making Windows absolute paths stable.
  return path.isAbsolute(value) ? path.normalize(value).toLowerCase() : value.toLowerCase();
}

function normalizeProbe(probe: AntigravityProbe): AntigravityProbe {
  // Some Windows AGY installations can answer `--version` but cannot write
  // their optional diagnostic files.  That should not be reported as an
  // authentication failure; the actual stream session remains authoritative.
  if (!probe.authenticated && !probe.authenticationRequired && /access is denied|permission denied/i.test(probe.message ?? ""))
    return { ...probe, authenticated: true };
  return probe;
}

export function getCachedAntigravityProbe(executable: string): AntigravityProbe | undefined {
  return probeEntries.get(executableKey(executable))?.result;
}

export function getAntigravityProbe(executable: string, cwd?: string): Promise<AntigravityProbe> {
  const key = executableKey(executable);
  const existing = probeEntries.get(key);
  if (existing) return existing.promise;

  const promise = probeAntigravity(executable, cwd).then(normalizeProbe);
  const entry: ProbeEntry = { promise };
  probeEntries.set(key, entry);
  void promise.then((result) => {
    entry.result = result;
  }, () => {
    // probeAntigravity currently resolves failures into a result, but do not
    // leave a rejected promise as a permanently unusable cache entry if that
    // implementation changes later.
    if (probeEntries.get(key) === entry) probeEntries.delete(key);
  });
  return promise;
}

export function warmAntigravityProbe(executable: string, cwd?: string): void {
  void getAntigravityProbe(executable, cwd).catch(() => undefined);
}

export function invalidateAntigravityProbe(executable: string): void {
  probeEntries.delete(executableKey(executable));
}

/** Test-only reset; no production caller should need to clear this cache. */
export function clearAntigravityProbeCache(): void {
  probeEntries.clear();
}
