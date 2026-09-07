import path from "node:path";
import { probeAntigravity } from "./antigravity.js";
// Model discovery is a process-wide concern.  A ManagerHost can own many
// visible conversations, but they all use the same Antigravity installation.
// Keeping the promise here prevents every conversation from starting its own
// `agy models` process when it is first selected.
const probeEntries = new Map();
function executableKey(executable) {
    const value = executable.trim();
    if (!value)
        return "agy";
    // Preserve bare PATH commands while making Windows absolute paths stable.
    return path.isAbsolute(value) ? path.normalize(value).toLowerCase() : value.toLowerCase();
}
function normalizeProbe(probe) {
    // Some Windows AGY installations can answer `--version` but cannot write
    // their optional diagnostic files.  That should not be reported as an
    // authentication failure; the actual stream session remains authoritative.
    if (!probe.authenticated && !probe.authenticationRequired && /access is denied|permission denied/i.test(probe.message ?? ""))
        return { ...probe, authenticated: true };
    return probe;
}
export function getCachedAntigravityProbe(executable) {
    return probeEntries.get(executableKey(executable))?.result;
}
export function getAntigravityProbe(executable, cwd) {
    const key = executableKey(executable);
    const existing = probeEntries.get(key);
    if (existing)
        return existing.promise;
    const promise = probeAntigravity(executable, cwd).then(normalizeProbe);
    const entry = { promise };
    probeEntries.set(key, entry);
    void promise.then((result) => {
        entry.result = result;
    }, () => {
        // probeAntigravity currently resolves failures into a result, but do not
        // leave a rejected promise as a permanently unusable cache entry if that
        // implementation changes later.
        if (probeEntries.get(key) === entry)
            probeEntries.delete(key);
    });
    return promise;
}
export function warmAntigravityProbe(executable, cwd) {
    void getAntigravityProbe(executable, cwd).catch(() => undefined);
}
export function invalidateAntigravityProbe(executable) {
    probeEntries.delete(executableKey(executable));
}
/** Test-only reset; no production caller should need to clear this cache. */
export function clearAntigravityProbeCache() {
    probeEntries.clear();
}
//# sourceMappingURL=antigravity-probe-cache.js.map