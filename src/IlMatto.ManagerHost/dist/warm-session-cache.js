/**
 * Keeps ManagerSession objects alive while bounding the number of actual
 * Antigravity CLI processes. Suspending a session never removes its state or
 * persisted transcript; it only asks the session to release its warm process.
 */
export class WarmAntigravitySessionCache {
    maximumWarmSessions;
    entries = new Map();
    sequence = 0;
    constructor(maximumWarmSessions = 3) {
        this.maximumWarmSessions = maximumWarmSessions;
        if (!Number.isInteger(maximumWarmSessions) || maximumWarmSessions < 1) {
            throw new Error("maximumWarmSessions must be a positive integer");
        }
    }
    register(session) {
        const existing = this.entries.get(session.id);
        this.entries.set(session.id, {
            session,
            lastUsedAt: existing?.lastUsedAt ?? Date.now(),
            sequence: existing?.sequence ?? ++this.sequence,
        });
    }
    touch(sessionId) {
        const entry = this.entries.get(sessionId);
        if (!entry)
            return;
        entry.lastUsedAt = Date.now();
        entry.sequence = ++this.sequence;
    }
    remove(sessionId) {
        this.entries.delete(sessionId);
    }
    trim(currentSessionId) {
        const evicted = [];
        const warmEntries = () => [...this.entries.values()].filter((entry) => entry.session.hasWarmProcess());
        while (warmEntries().length > this.maximumWarmSessions) {
            const candidate = warmEntries()
                .filter((entry) => entry.session.id !== currentSessionId && !entry.session.isBusy())
                .sort((left, right) => left.lastUsedAt - right.lastUsedAt || left.sequence - right.sequence)[0];
            if (!candidate)
                break;
            candidate.session.suspendAntigravity();
            evicted.push(candidate.session.id);
        }
        return evicted;
    }
    get warmSessionIds() {
        return [...this.entries.values()]
            .filter((entry) => entry.session.hasWarmProcess())
            .sort((left, right) => right.lastUsedAt - left.lastUsedAt || right.sequence - left.sequence)
            .map((entry) => entry.session.id);
    }
    get warmCount() {
        return this.warmSessionIds.length;
    }
}
//# sourceMappingURL=warm-session-cache.js.map