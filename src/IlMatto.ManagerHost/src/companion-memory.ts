import { appendFile, mkdir, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import path from "node:path";
import type { ProfilePatch, ProfileSection, SessionSummary, SessionSummaryPatch, TranscriptEntry } from "./protocol.js";

export const PROFILE_MAX_CHARS = 8_000;
export const SUMMARY_MAX_CHARS = 6_000;
export const MAX_KEY_EVENTS = 20;
export const MAX_OPEN_LOOPS = 10;
export const MAX_KEYWORDS = 20;
export const MAX_SEARCH_RESULTS = 5;
export const MAX_SNIPPETS = 3;
export const MAX_SNIPPET_CHARS = 6_000;

export type SessionSearchResult = Pick<SessionSummary, "sessionId" | "title" | "summary" | "keyEvents" | "openLoops" | "keywords" | "updatedAt"> & {
  score: number;
};

export type SessionSnippet = {
  messages: TranscriptEntry[];
};

export type SessionOpenResult = {
  sessionId: string;
  snippets: SessionSnippet[];
  truncated: boolean;
};

const PROFILE_SECTION_HEADINGS: Record<ProfileSection, string> = {
  basic: "基本信息",
  interests: "兴趣",
  preferences: "互动偏好",
  boundaries: "边界与注意事项",
  current_topics: "当前关注",
};

const DEFAULT_PROFILE = `# 用户画像\n\n## 基本信息\n\n## 兴趣\n\n## 互动偏好\n\n## 边界与注意事项\n\n## 当前关注\n`;

// Multiple Manager sessions share profile.md. Serialize read/merge/write
// cycles by target file so concurrent updates do not silently overwrite one
// another while still keeping the storage implementation process-local.
const writeQueues = new Map<string, Promise<void>>();

export class CompanionMemoryStore {
  private readonly sessionsRoot: string;

  constructor(private readonly root: string) {
    this.sessionsRoot = path.join(root, "sessions");
  }

  get profilePath(): string { return path.join(this.root, "profile.md"); }

  getSessionDirectory(sessionId: string): string {
    return path.join(this.sessionsRoot, safeSessionId(sessionId));
  }

  async initialize(): Promise<void> {
    await mkdir(this.sessionsRoot, { recursive: true });
  }

  async readProfile(seed = ""): Promise<string> {
    await this.initialize();
    try {
      return clip(await readFile(this.profilePath, "utf8"), PROFILE_MAX_CHARS).trim();
    } catch (error: any) {
      if (error?.code !== "ENOENT") return "";
      const initial = seed.trim() ? clip(seed.trim(), PROFILE_MAX_CHARS) : DEFAULT_PROFILE;
      await writeAtomic(this.profilePath, `${initial.trim()}\n`);
      return initial.trim();
    }
  }

  async updateProfile(patch: ProfilePatch): Promise<string> {
    return withWriteLock(this.profilePath, async () => {
      await this.initialize();
      const existing = await this.readProfile();
      const updated = mergeProfile(existing, patch);
      await writeAtomic(this.profilePath, `${clip(updated, PROFILE_MAX_CHARS).trim()}\n`);
      return clip(updated, PROFILE_MAX_CHARS).trim();
    });
  }

  async readSummary(sessionId: string, title = sessionId): Promise<SessionSummary> {
    const safeId = safeSessionId(sessionId);
    const filePath = path.join(this.getSessionDirectory(safeId), "summary.json");
    try {
      const parsed = JSON.parse(await readFile(filePath, "utf8")) as Partial<SessionSummary>;
      return normalizeSummary(parsed, safeId, title);
    } catch {
      return emptySummary(safeId, title);
    }
  }

  async updateSummary(sessionId: string, patch: SessionSummaryPatch, title = sessionId): Promise<SessionSummary> {
    const safeId = safeSessionId(sessionId);
    const summaryPath = path.join(this.getSessionDirectory(safeId), "summary.json");
    return withWriteLock(summaryPath, async () => {
      const current = await this.readSummary(safeId, title);
      const next: SessionSummary = {
        ...current,
        title: clip(patch.title?.trim() || current.title || title, 160),
        summary: clip(joinSummary(current.summary, patch.summaryPatch), SUMMARY_MAX_CHARS),
        keyEvents: mergeList(current.keyEvents, patch.keyEvents, MAX_KEY_EVENTS),
        openLoops: mergeList(current.openLoops, patch.openLoops, MAX_OPEN_LOOPS),
        keywords: mergeList(current.keywords, patch.keywords, MAX_KEYWORDS),
        updatedAt: new Date().toISOString(),
      };
      await mkdir(this.getSessionDirectory(safeId), { recursive: true });
      await writeAtomic(summaryPath, `${JSON.stringify(next, null, 2)}\n`);
      return next;
    });
  }

  async appendTranscript(sessionId: string, entries: readonly TranscriptEntry[]): Promise<void> {
    const safeId = safeSessionId(sessionId);
    const valid = entries
      .filter((entry) => (entry.role === "user" || entry.role === "assistant") && entry.text.trim().length > 0)
      .map((entry) => ({
        role: entry.role,
        text: clip(entry.text.trim(), 64_000),
        createdAt: entry.createdAt || new Date().toISOString(),
        ...(entry.turnId ? { turnId: entry.turnId } : {}),
      }));
    if (valid.length === 0) return;
    const directory = this.getSessionDirectory(safeId);
    await mkdir(directory, { recursive: true });
    await appendFile(path.join(directory, "transcript.jsonl"), valid.map((entry) => JSON.stringify(entry)).join("\n") + "\n", "utf8");
  }

  async ensureTranscript(sessionId: string, history: readonly TranscriptEntry[]): Promise<void> {
    const safeId = safeSessionId(sessionId);
    const transcriptPath = path.join(this.getSessionDirectory(safeId), "transcript.jsonl");
    try {
      await stat(transcriptPath);
      return;
    } catch (error: any) {
      if (error?.code !== "ENOENT") return;
    }
    await this.appendTranscript(safeId, history);
  }

  /** Remove only one conversation's memory. The shared profile is deliberately
   * outside this directory and is never touched by session deletion. */
  async deleteSession(sessionId: string): Promise<void> {
    const safeId = safeSessionId(sessionId);
    await rm(this.getSessionDirectory(safeId), { recursive: true, force: true });
  }

  async searchSessions(query: string, limit = MAX_SEARCH_RESULTS): Promise<SessionSearchResult[]> {
    await this.initialize();
    const requestedLimit = Math.max(1, Math.min(MAX_SEARCH_RESULTS, Math.trunc(limit)));
    const queryText = normalizeSearchText(query);
    if (!queryText) return [];
    const terms = searchTerms(queryText);
    const entries = await readdir(this.sessionsRoot, { withFileTypes: true }).catch(() => []);
    const results: SessionSearchResult[] = [];
    for (const entry of entries) {
      if (!entry.isDirectory() || !isSafeSessionId(entry.name)) continue;
      const summaryPath = path.join(this.sessionsRoot, entry.name, "summary.json");
      let summary: SessionSummary;
      try {
        summary = normalizeSummary(JSON.parse(await readFile(summaryPath, "utf8")), entry.name, entry.name);
      } catch {
        continue;
      }
      const haystack = normalizeSearchText([
        summary.title,
        summary.summary,
        ...summary.keyEvents,
        ...summary.openLoops,
        ...summary.keywords,
      ].join("\n"));
      const score = scoreSearch(queryText, terms, haystack, summary.updatedAt);
      if (score <= 0) continue;
      results.push({ ...summary, score });
    }
    return results.sort((left, right) => right.score - left.score || right.updatedAt.localeCompare(left.updatedAt)).slice(0, requestedLimit);
  }

  async openSession(sessionId: string, query: string): Promise<SessionOpenResult> {
    const safeId = safeSessionId(sessionId);
    const transcriptPath = path.join(this.getSessionDirectory(safeId), "transcript.jsonl");
    let entries: TranscriptEntry[] = [];
    try {
      const raw = await readFile(transcriptPath, "utf8");
      entries = raw.split(/\r?\n/).filter(Boolean).flatMap((line) => {
        try {
          const entry = JSON.parse(line);
          return isTranscriptEntry(entry) ? [entry] : [];
        } catch {
          // A process interrupted during append must not hide all earlier
          // visible transcript lines from a later bounded lookup.
          return [];
        }
      });
    } catch {
      return { sessionId: safeId, snippets: [], truncated: false };
    }
    const queryText = normalizeSearchText(query);
    const terms = searchTerms(queryText);
    const hits = entries.map((entry, index) => ({ entry, index, score: scoreSearch(queryText, terms, normalizeSearchText(entry.text), entry.createdAt) }))
      .filter((item) => item.score > 0)
      .sort((left, right) => right.score - left.score || left.index - right.index);
    const selected = new Set<number>();
    const snippets: SessionSnippet[] = [];
    let usedChars = 0;
    for (const hit of hits) {
      if (snippets.length >= MAX_SNIPPETS) break;
      const start = Math.max(0, hit.index - 1);
      const end = Math.min(entries.length, hit.index + 2);
      const indexes = Array.from({ length: end - start }, (_, offset) => start + offset);
      if (indexes.some((index) => selected.has(index))) continue;
      const messages = indexes.map((index) => entries[index]);
      const boundedMessages = fitSnippetMessages(messages, MAX_SNIPPET_CHARS - usedChars);
      if (boundedMessages.length === 0) continue;
      indexes.forEach((index) => selected.add(index));
      snippets.push({ messages: boundedMessages });
      usedChars += boundedMessages.reduce((total, message) => total + message.text.length, 0);
    }
    return { sessionId: safeId, snippets, truncated: hits.length > snippets.length };
  }
}

export function safeSessionId(value: string): string {
  if (!isSafeSessionId(value)) throw new Error("无效的 Manager 会话标识。");
  return value;
}

function isSafeSessionId(value: string): boolean {
  return /^[A-Za-z0-9_-]{1,160}$/.test(value);
}

function emptySummary(sessionId: string, title: string): SessionSummary {
  return { version: 1, sessionId, title: clip(title || sessionId, 160), summary: "", keyEvents: [], openLoops: [], keywords: [], updatedAt: new Date(0).toISOString() };
}

function normalizeSummary(value: Partial<SessionSummary> | unknown, sessionId: string, title: string): SessionSummary {
  const item = value && typeof value === "object" ? value as Record<string, unknown> : {};
  return {
    version: 1,
    sessionId,
    title: clip(typeof item.title === "string" && item.title.trim() ? item.title.trim() : title, 160),
    summary: clip(typeof item.summary === "string" ? item.summary.trim() : "", SUMMARY_MAX_CHARS),
    keyEvents: normalizeList(item.keyEvents, MAX_KEY_EVENTS),
    openLoops: normalizeList(item.openLoops, MAX_OPEN_LOOPS),
    keywords: normalizeList(item.keywords, MAX_KEYWORDS),
    updatedAt: typeof item.updatedAt === "string" && item.updatedAt ? item.updatedAt : new Date(0).toISOString(),
  };
}

function normalizeList(value: unknown, limit: number): string[] {
  return Array.isArray(value) ? mergeList([], value.filter((item): item is string => typeof item === "string"), limit) : [];
}

function mergeList(current: readonly string[], additions: readonly string[] | undefined, limit: number): string[] {
  const result = [...current];
  for (const item of additions ?? []) {
    const normalized = item.trim();
    if (!normalized || result.some((existing) => normalizeForCompare(existing) === normalizeForCompare(normalized))) continue;
    result.push(clip(normalized, 500));
  }
  return result.slice(-limit);
}

function joinSummary(current: string, patch: string | undefined): string {
  const addition = patch?.trim();
  if (!addition) return current.trim();
  if (!current.trim()) return addition;
  if (normalizeForCompare(current).includes(normalizeForCompare(addition))) return current.trim();
  return `${current.trim()} ${addition}`.replace(/\s+/g, " ").trim();
}

function mergeProfile(existing: string, patch: ProfilePatch): string {
  const sectionHeading = PROFILE_SECTION_HEADINGS[patch.section];
  const source = existing.trim() || DEFAULT_PROFILE.trim();
  const lines = source.split(/\r?\n/);
  let start = lines.findIndex((line) => line.trim() === `## ${sectionHeading}`);
  if (start < 0) {
    lines.push("", `## ${sectionHeading}`);
    start = lines.length - 1;
  }
  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (/^##\s+/.test(lines[index].trim())) { end = index; break; }
  }
  const body = lines.slice(start + 1, end);
  const removed = new Set((patch.remove ?? []).map(normalizeForCompare));
  const kept = body.filter((line) => !removed.has(normalizeForCompare(line.replace(/^[-*]\s+/, "").trim())));
  for (const item of patch.add ?? []) {
    const normalized = item.trim();
    if (!normalized) continue;
    const comparable = normalizeForCompare(normalized);
    if (!kept.some((line) => normalizeForCompare(line.replace(/^[-*]\s+/, "").trim()) === comparable)) kept.push(`- ${clip(normalized, 500)}`);
  }
  lines.splice(start + 1, end - start - 1, ...kept);
  return lines.join("\n").replace(/\n{3,}/g, "\n\n").trim();
}

function normalizeForCompare(value: string): string { return value.toLowerCase().replace(/\s+/g, " ").trim(); }
function normalizeSearchText(value: string): string { return value.toLowerCase().replace(/\s+/g, " ").trim(); }
function searchTerms(value: string): string[] { return value.match(/[a-z0-9_]+|[\u4e00-\u9fff]+/gi) ?? [value]; }

function scoreSearch(query: string, terms: readonly string[], haystack: string, date: string): number {
  if (!haystack) return 0;
  let score = haystack.includes(query) ? 10 : 0;
  for (const term of terms) if (term && haystack.includes(term)) score += Math.max(1, Math.min(4, term.length / 2));
  const timestamp = Date.parse(date);
  if (score > 0 && Number.isFinite(timestamp)) score += Math.max(0, Math.min(1, (timestamp - Date.now() + 31_536_000_000) / 31_536_000_000));
  return score;
}

function isTranscriptEntry(value: unknown): value is TranscriptEntry {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  return (item.role === "user" || item.role === "assistant") && typeof item.text === "string" && item.text.trim().length > 0 && typeof item.createdAt === "string";
}

function fitSnippetMessages(messages: readonly TranscriptEntry[], budget: number): TranscriptEntry[] {
  let remaining = Math.max(0, budget);
  const result: TranscriptEntry[] = [];
  for (const message of messages) {
    if (remaining <= 0) break;
    const text = message.text.length <= remaining
      ? message.text
      : remaining > 12
        ? `${message.text.slice(0, remaining - 12).trimEnd()}\n…（已截断）`
        : message.text.slice(0, remaining);
    if (!text.trim()) break;
    result.push({ ...message, text });
    remaining -= text.length;
  }
  return result;
}

function clip(value: string, maxChars: number): string {
  if (value.length <= maxChars) return value;
  const marker = "\n…（已截断）";
  if (maxChars <= marker.length) return marker.slice(0, maxChars);
  return `${value.slice(0, maxChars - marker.length).trimEnd()}${marker}`;
}

async function writeAtomic(filePath: string, content: string): Promise<void> {
  await mkdir(path.dirname(filePath), { recursive: true });
  const temporary = `${filePath}.${process.pid}.${randomUUID()}.tmp`;
  await writeFile(temporary, content, "utf8");
  await rename(temporary, filePath);
}

async function withWriteLock<T>(filePath: string, operation: () => Promise<T>): Promise<T> {
  const previous = writeQueues.get(filePath) ?? Promise.resolve();
  let release!: () => void;
  const current = new Promise<void>((resolve) => { release = resolve; });
  writeQueues.set(filePath, current);
  await previous;
  try {
    return await operation();
  } finally {
    release();
    if (writeQueues.get(filePath) === current) writeQueues.delete(filePath);
  }
}
