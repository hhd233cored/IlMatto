export interface WarmAntigravitySession {
  readonly id: string;
  isBusy(): boolean;
  hasWarmProcess(): boolean;
  suspendAntigravity(): void;
}

type Entry = {
  session: WarmAntigravitySession;
  lastUsedAt: number;
  sequence: number;
};

/**
 * Keeps ManagerSession objects alive while bounding the number of actual
 * Antigravity CLI processes. Suspending a session never removes its state or
 * persisted transcript; it only asks the session to release its warm process.
 */
export class WarmAntigravitySessionCache {
  private readonly entries = new Map<string, Entry>();
  private sequence = 0;

  constructor(private readonly maximumWarmSessions = 3) {
    if (!Number.isInteger(maximumWarmSessions) || maximumWarmSessions < 1) {
      throw new Error("maximumWarmSessions must be a positive integer");
    }
  }

  register(session: WarmAntigravitySession): void {
    const existing = this.entries.get(session.id);
    this.entries.set(session.id, {
      session,
      lastUsedAt: existing?.lastUsedAt ?? Date.now(),
      sequence: existing?.sequence ?? ++this.sequence,
    });
  }

  touch(sessionId: string): void {
    const entry = this.entries.get(sessionId);
    if (!entry) return;
    entry.lastUsedAt = Date.now();
    entry.sequence = ++this.sequence;
  }

  remove(sessionId: string): void {
    this.entries.delete(sessionId);
  }

  trim(currentSessionId?: string): string[] {
    const evicted: string[] = [];
    const warmEntries = () => [...this.entries.values()].filter((entry) => entry.session.hasWarmProcess());
    while (warmEntries().length > this.maximumWarmSessions) {
      const candidate = warmEntries()
        .filter((entry) => entry.session.id !== currentSessionId && !entry.session.isBusy())
        .sort((left, right) => left.lastUsedAt - right.lastUsedAt || left.sequence - right.sequence)[0];
      if (!candidate) break;
      candidate.session.suspendAntigravity();
      evicted.push(candidate.session.id);
    }
    return evicted;
  }

  get warmSessionIds(): string[] {
    return [...this.entries.values()]
      .filter((entry) => entry.session.hasWarmProcess())
      .sort((left, right) => right.lastUsedAt - left.lastUsedAt || right.sequence - left.sequence)
      .map((entry) => entry.session.id);
  }

  get warmCount(): number {
    return this.warmSessionIds.length;
  }
}
