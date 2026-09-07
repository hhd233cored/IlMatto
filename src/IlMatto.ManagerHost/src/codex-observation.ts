import { execFile } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import os from "node:os";
import path from "node:path";
import { existsSync } from "node:fs";
import { appendFile, mkdir, readdir, readFile, rm, writeFile } from "node:fs/promises";
import type {
  CodexObservationOperation,
  CodexObservationStatus,
  CodexTaskDraft,
  CodexTaskReport,
  CodexTaskState,
  ManagerImageAttachment,
} from "./protocol.js";
import { CodexAppServerBridge, CodexWorkerError, type CodexWorkerSettings } from "./codex-worker.js";
import type { CodingWorkerEvent } from "./coding-worker.js";

export type CodexObservationRequest = {
  sessionId: string;
  requestId: string;
  operation: CodexObservationOperation;
  workspacePath?: string;
  prompt?: string;
  attachments?: ManagerImageAttachment[];
  taskId?: string;
  maxBytes?: number;
};

export type CodexObservationResult = {
  ok: boolean;
  data?: unknown;
  error?: { code: string; message: string };
};

export type CodexObservationControllerOptions = {
  sessionId: string;
  workspacePath: string;
  executable?: string;
  model?: string;
  effort?: string;
  approvalPolicy?: string;
  sandboxMode?: string;
  onInteraction: (request: {
    requestId: string;
    kind: string;
    title: string;
    details: string;
    command?: string;
    diff?: string;
    fields?: unknown;
    url?: string;
  }) => void;
  /** Notify the desktop that Antigravity created a draft to be edited in the
   * normal input box. This is deliberately separate from onInteraction:
   * creating a draft must not create an approval card or start Codex. */
  onDraft?: (draft: CodexTaskDraft) => void;
  onStatus?: (status: CodexObservationStatus) => void;
  /** Called after a task id is allocated but before the task becomes active. */
  onTaskStart?: (taskId: string, startedAt: string) => void | Promise<void>;
  /** Called once a task has reached a terminal state and its report is saved. */
  onTaskFinished?: (taskId: string, state: Exclude<CodexTaskState, "draft" | "queued" | "running" | "awaiting_user_input">) => void;
  /** User-facing projection for the desktop coding bubble. */
  onEvent?: (event: CodexObservationProgressEvent) => void;
  bridgeFactory?: (settings: CodexWorkerSettings, onEvent: (event: CodingWorkerEvent) => void) => CodexAppServerBridge;
};

export type CodexObservationProgressEvent =
  | { type: "assistant_delta"; taskId: string; text: string }
  | { type: "thinking_delta"; taskId: string; text: string }
  | { type: "tool_started"; taskId: string; callId: string; tool: string; command?: string }
  | { type: "tool_output"; taskId: string; callId: string; tool: string; text: string }
  | { type: "tool_completed"; taskId: string; callId: string; tool: string; ok: boolean; summary: string; command?: string; output?: string; diff?: string }
  | { type: "completed"; taskId: string; status: "completed" | "failed" | "cancelled" | "partial"; text: string; startedAt?: string; completedAt?: string; durationMs?: number };

type SessionIndexEntry = { threadId?: string; latestTaskId?: string; taskIds?: string[] };
type SessionIndex = Record<string, SessionIndexEntry>;

type ActiveTask = {
  taskId: string;
  prompt: string;
  attachments: ManagerImageAttachment[];
  startedAt: string;
  startedAtMs: number;
  events: number;
  finished: boolean;
  commands: Array<{ command: string; exitCode?: number; summary?: string }>;
  tests: Array<{ name?: string; status: "passed" | "failed" | "skipped" | "unknown"; summary?: string }>;
  changedFiles: Array<{ path: string; additions?: number; deletions?: number }>;
  warnings: string[];
  pendingQuestions: string[];
  diff: string;
  finalText: string;
  /**
   * The assistant text that has actually been projected to the desktop. Keep
   * the prefix (rather than the tail) so completion reconciliation still works
   * when a long turn is truncated for storage/display.
   */
  streamedText: string;
  phase?: string;
};

export class CodexObservationStore {
  readonly root: string;
  private readonly indexPath: string;
  private indexQueue: Promise<void> = Promise.resolve();

  constructor(root = defaultObservationRoot()) {
    this.root = path.resolve(root);
    this.indexPath = path.join(this.root, "session-map.json");
  }

  async createDraft(sessionId: string, workspacePath: string, prompt: string, attachments: ManagerImageAttachment[]): Promise<CodexTaskDraft> {
    await mkdir(this.root, { recursive: true });
    const draftId = `draft-${randomUUID()}`;
    const createdAt = new Date().toISOString();
    const draft: CodexTaskDraft = {
      draftId,
      sessionId,
      workspacePath,
      prompt,
      attachments,
      promptHash: hashPrompt(prompt),
      createdAt,
      expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
      state: "awaiting_user_confirmation",
    };
    await writeFile(this.draftPath(draftId), JSON.stringify(draft, null, 2), "utf8");
    return draft;
  }

  async readDraft(draftId: string): Promise<CodexTaskDraft | undefined> {
    try {
      const draft = JSON.parse(await readFile(this.draftPath(draftId), "utf8")) as CodexTaskDraft;
      if (draft.expiresAt && Date.parse(draft.expiresAt) <= Date.now()) {
        await rm(this.draftPath(draftId), { force: true });
        return undefined;
      }
      return draft;
    } catch { return undefined; }
  }

  async deleteDraft(draftId: string): Promise<void> {
    await rm(this.draftPath(draftId), { force: true });
  }

  async saveReport(report: CodexTaskReport, diff = ""): Promise<void> {
    await mkdir(this.root, { recursive: true });
    await writeFile(this.reportPath(report.taskId), JSON.stringify(report, null, 2), "utf8");
    if (diff) await writeFile(this.diffPath(report.taskId), diff, "utf8");
    await this.updateIndex(report.sessionId, (entry) => {
      entry.latestTaskId = report.taskId;
      entry.taskIds = [...new Set([...(entry.taskIds ?? []), report.taskId])].slice(-100);
    });
  }

  async appendEvent(taskId: string, event: Record<string, unknown>): Promise<void> {
    await mkdir(this.root, { recursive: true });
    const safe = {
      timestamp: new Date().toISOString(),
      type: String(event.type ?? "event"),
      text: redactObservationText(event.text ?? event.message ?? "", 8_000),
      details: redactObservationDetails(event),
    };
    await appendFile(this.eventPath(taskId), `${JSON.stringify(safe)}\n`, "utf8");
  }

  async getReport(sessionId: string, taskId?: string): Promise<CodexTaskReport | undefined> {
    const index = await this.readIndex();
    const entry = index[sessionId];
    const selected = taskId ?? entry?.latestTaskId;
    if (!selected || (taskId && !(entry?.taskIds ?? []).includes(taskId))) return undefined;
    try {
      const report = JSON.parse(await readFile(this.reportPath(selected), "utf8")) as CodexTaskReport;
      return report.sessionId === sessionId ? report : undefined;
    } catch { return undefined; }
  }

  async getDiff(sessionId: string, taskId: string, maxBytes = 256_000): Promise<{ taskId: string; diff: string } | undefined> {
    const report = await this.getReport(sessionId, taskId);
    if (!report) return undefined;
    try {
      const diff = await readFile(this.diffPath(taskId), "utf8");
      if (diff.length <= maxBytes) return { taskId, diff };
      const suffix = "\n…[diff 已截断]";
      return { taskId, diff: maxBytes <= suffix.length ? suffix.slice(0, maxBytes) : `${diff.slice(0, maxBytes - suffix.length)}${suffix}` };
    } catch { return { taskId, diff: "" }; }
  }

  /** Remove only Codex observation artifacts owned by one Manager session. */
  async deleteSession(sessionId: string): Promise<void> {
    if (!isSafeFileStem(sessionId)) throw new Error("无效的 Manager 会话标识。");
    const previous = this.indexQueue;
    const operation = previous.then(async () => {
      const index = await this.readIndexFile();
      const entry = index[sessionId];
      const taskIds = new Set((entry?.taskIds ?? []).filter(isSafeFileStem));
      if (entry?.latestTaskId && isSafeFileStem(entry.latestTaskId)) taskIds.add(entry.latestTaskId);

      const draftFiles: string[] = [];
      for (const name of await readdir(this.root).catch(() => [] as string[])) {
        if (!/^draft-[A-Za-z0-9_-]+\.json$/.test(name)) continue;
        try {
          const draft = JSON.parse(await readFile(path.join(this.root, name), "utf8")) as { sessionId?: unknown };
          if (draft.sessionId === sessionId) draftFiles.push(path.join(this.root, name));
        } catch { }
      }

      delete index[sessionId];
      if (Object.keys(index).length === 0) await rm(this.indexPath, { force: true });
      else await writeFile(this.indexPath, JSON.stringify(index, null, 2), "utf8");

      const taskFiles = [...taskIds].flatMap((taskId) => [
        this.draftPath(taskId), this.eventPath(taskId), this.reportPath(taskId), this.diffPath(taskId),
      ]);
      await Promise.all([...draftFiles, ...taskFiles].map((file) => rm(file, { force: true })));
    });
    this.indexQueue = operation.catch(() => undefined);
    await operation;
  }

  async getThreadId(sessionId: string): Promise<string | undefined> {
    return (await this.readIndex())[sessionId]?.threadId;
  }

  async setThreadId(sessionId: string, threadId: string): Promise<void> {
    await this.updateIndex(sessionId, (entry) => { entry.threadId = threadId; });
  }

  private async readIndex(): Promise<SessionIndex> {
    await this.indexQueue;
    return this.readIndexFile();
  }

  private async readIndexFile(): Promise<SessionIndex> {
    try {
      const value = JSON.parse(await readFile(this.indexPath, "utf8"));
      return value && typeof value === "object" && !Array.isArray(value) ? value as SessionIndex : {};
    } catch { return {}; }
  }

  private async updateIndex(sessionId: string, update: (entry: SessionIndexEntry) => void): Promise<void> {
    const previous = this.indexQueue;
    const operation = previous.then(async () => {
      await mkdir(this.root, { recursive: true });
      const index = await this.readIndexFile();
      const entry = index[sessionId] ?? {};
      update(entry);
      index[sessionId] = entry;
      await writeFile(this.indexPath, JSON.stringify(index, null, 2), "utf8");
    });
    this.indexQueue = operation.catch(() => undefined);
    await operation;
  }

  private draftPath(id: string): string { return path.join(this.root, `${id}.json`); }
  private eventPath(id: string): string { return path.join(this.root, `${id}.events.jsonl`); }
  private reportPath(id: string): string { return path.join(this.root, `${id}.report.json`); }
  private diffPath(id: string): string { return path.join(this.root, `${id}.diff.txt`); }
}

export class CodexObservationController {
  readonly store: CodexObservationStore;
  private readonly sessionId: string;
  private readonly workspacePath: string;
  private executable?: string;
  private model?: string;
  private effort?: string;
  private approvalPolicy?: string;
  private sandboxMode?: string;
  private readonly onInteraction: CodexObservationControllerOptions["onInteraction"];
  private readonly onDraft?: CodexObservationControllerOptions["onDraft"];
  private readonly onStatus?: CodexObservationControllerOptions["onStatus"];
  private readonly onTaskStart?: CodexObservationControllerOptions["onTaskStart"];
  private readonly onTaskFinished?: CodexObservationControllerOptions["onTaskFinished"];
  private readonly onEvent?: CodexObservationControllerOptions["onEvent"];
  private readonly bridgeFactory?: CodexObservationControllerOptions["bridgeFactory"];
  private readonly drafts = new Map<string, CodexTaskDraft>();
  private bridge?: CodexAppServerBridge;
  private activeTask?: ActiveTask;
  private currentStatus: CodexObservationStatus;
  private eventQueue: Promise<void> = Promise.resolve();
  private cancelRequested = false;

  constructor(options: CodexObservationControllerOptions, store = new CodexObservationStore()) {
    this.store = store;
    this.sessionId = options.sessionId;
    this.workspacePath = path.resolve(options.workspacePath);
    const configuredExecutable = options.executable?.trim() || process.env.ILMATTO_CODEX_EXECUTABLE?.trim() || undefined;
    // Codex Desktop installs versioned binaries and removes old directories
    // during updates. If a persisted absolute path is stale, let the bridge
    // resolve the current `codex` command from PATH instead of surfacing an
    // avoidable ENOENT. Relative/custom commands are preserved unchanged.
    this.executable = configuredExecutable && path.isAbsolute(configuredExecutable) && !existsSync(configuredExecutable)
      ? undefined
      : configuredExecutable;
    this.model = options.model?.trim() || process.env.ILMATTO_CODEX_MODEL || undefined;
    this.effort = options.effort?.trim() || process.env.ILMATTO_CODEX_EFFORT || undefined;
    this.approvalPolicy = options.approvalPolicy?.trim() || process.env.ILMATTO_CODEX_APPROVAL_POLICY || undefined;
    this.sandboxMode = options.sandboxMode?.trim() || process.env.ILMATTO_CODEX_SANDBOX_MODE || undefined;
    this.onInteraction = options.onInteraction;
    this.onDraft = options.onDraft;
    this.onStatus = options.onStatus;
    this.onTaskStart = options.onTaskStart;
    this.onTaskFinished = options.onTaskFinished;
    this.onEvent = options.onEvent;
    this.bridgeFactory = options.bridgeFactory;
    this.currentStatus = { sessionId: this.sessionId, state: "idle", reportAvailable: false };
  }

  /** Apply a newly selected native Codex approval policy to subsequent turns.
   * The current turn, if any, is intentionally left untouched. */
  setApprovalPolicy(policy?: string): void {
    this.approvalPolicy = policy?.trim() || undefined;
    this.bridge?.setApprovalPolicy(this.approvalPolicy);
  }

  /** Apply a newly selected native Codex sandbox mode to subsequent turns. */
  setSandboxMode(mode?: string): void {
    this.sandboxMode = mode?.trim() || undefined;
    this.bridge?.setSandboxMode(this.sandboxMode);
  }

  /** Update the optional Codex model and reasoning effort for the next turn.
   * The current turn remains untouched, matching policy/sandbox semantics. */
  setModel(model?: string): void {
    this.model = model?.trim() || undefined;
    this.bridge?.setModel(this.model);
  }

  setEffort(effort?: string): void {
    this.effort = effort?.trim() || undefined;
    this.bridge?.setEffort(this.effort);
  }

  async handle(request: CodexObservationRequest): Promise<CodexObservationResult> {
    if (request.sessionId !== this.sessionId) return fail("SESSION_MISMATCH", "Codex 观察请求不属于当前 Manager 会话。");
    try {
      switch (request.operation) {
        case "draft_codex_task": return { ok: true, data: await this.createDraft(request) };
        case "get_codex_status": return { ok: true, data: await this.status() };
        case "get_latest_codex_report": return { ok: true, data: await this.readReport() ?? null };
        case "get_codex_report": {
          if (!request.taskId) return fail("INVALID_REQUEST", "缺少 taskId。");
          return { ok: true, data: await this.readReport(request.taskId) ?? null };
        }
        case "get_codex_diff": {
          if (!request.taskId) return fail("INVALID_REQUEST", "缺少 taskId。");
          return { ok: true, data: await this.store.getDiff(this.sessionId, request.taskId, request.maxBytes) ?? null };
        }
      }
    } catch (error) {
      return fail(errorCode(error), error instanceof Error ? error.message : "Codex 观察请求失败。");
    }
  }

  async resolveInteraction(requestId: string, approved: boolean, values?: Record<string, unknown>): Promise<void> {
    const draft = this.drafts.get(requestId) ?? await this.store.readDraft(requestId);
    if (draft) {
      if (!approved) {
        this.drafts.delete(requestId);
        await this.store.deleteDraft(requestId);
        this.setStatus({ state: "idle", reportAvailable: await this.store.getReport(this.sessionId) !== undefined, message: "Codex 草稿已被用户拒绝。" });
        return;
      }
      if (hashPrompt(draft.prompt) !== draft.promptHash) throw new Error("Codex 草稿校验失败：Prompt 已被修改或损坏。");
      if (this.activeTask && !this.activeTask.finished) throw new Error("Codex 已有任务正在运行。");
      const edited = values?.answer ?? values?.prompt;
      const prompt = typeof edited === "string" && edited.trim() ? edited : draft.prompt;
      if (!prompt.trim()) throw new Error("Codex Prompt 不能为空。");
      if (path.resolve(draft.workspacePath).toLowerCase() !== this.workspacePath.toLowerCase()) throw new Error("Codex 草稿工作区已发生变化。");
      this.drafts.delete(requestId);
      await this.store.deleteDraft(requestId);
      await this.startTask(prompt, draft.attachments, "Codex 任务已确认，正在启动。");
      return;
    }
    if (!this.bridge) throw new Error("Codex 交互请求不存在或已完成。");
    this.bridge.resolve(requestId, approved, values);
  }

  /** Start a Codex task from an explicit user command (`@codex ...`) or from
   * a previously created draft. The final prompt is supplied by the user and
   * is therefore authoritative; only the draft/session/workspace binding is
   * validated here. */
  async submitPrompt(prompt: string, attachments: ManagerImageAttachment[] = [], draftId?: string): Promise<string> {
    const normalized = prompt.trim();
    if (!normalized) throw new Error("Codex Prompt 不能为空。");
    if (this.activeTask && !this.activeTask.finished) throw new Error("Codex 已有任务正在运行。");

    let finalAttachments = attachments;
    if (draftId) {
      const draft = this.drafts.get(draftId) ?? await this.store.readDraft(draftId);
      if (!draft) throw new Error("Codex 草稿不存在或已过期。");
      if (draft.sessionId !== this.sessionId) throw new Error("Codex 草稿不属于当前 Manager 会话。");
      if (path.resolve(draft.workspacePath).toLowerCase() !== this.workspacePath.toLowerCase()) throw new Error("Codex 草稿工作区已发生变化。");
      if (finalAttachments.length === 0) finalAttachments = draft.attachments;
      this.drafts.delete(draftId);
      await this.store.deleteDraft(draftId);
    }

    return this.startTask(normalized, finalAttachments, "Codex 任务已提交，正在启动。");
  }

  cancel(): void {
    if (!this.activeTask || this.activeTask.finished) return;
    this.cancelRequested = true;
    if (this.bridge) {
      this.bridge.cancel();
      return;
    }
    // The App Server may still be connecting when the user cancels. Finish
    // the task immediately and let runTask observe the terminal flag before it
    // can send a turn/start request.
    void this.finishTask(this.activeTask, "cancelled", "Codex 任务已取消。", "cancelled");
  }

  async dispose(): Promise<void> {
    const active = this.activeTask;
    if (active && !active.finished) {
      active.finished = true;
      await this.finishTask(active, "cancelled", "Codex 任务已随 Manager 关闭。", "cancelled");
    }
    await this.eventQueue;
    await this.bridge?.dispose();
    this.bridge = undefined;
  }

  async deleteLocalData(): Promise<void> {
    await this.store.deleteSession(this.sessionId);
  }

  private async createDraft(request: CodexObservationRequest): Promise<CodexTaskDraft> {
    if (this.activeTask && !this.activeTask.finished) return Promise.reject(new Error("Codex 正在处理另一个任务。"));
    const prompt = request.prompt?.trim() ?? "";
    if (!prompt) return Promise.reject(new Error("Codex Prompt 不能为空。"));
    const workspacePath = path.resolve(request.workspacePath ?? this.workspacePath);
    if (workspacePath.toLowerCase() !== this.workspacePath.toLowerCase()) return Promise.reject(new Error("Codex 任务工作区必须与当前 Manager 工作区一致。"));
    const attachments = (request.attachments ?? []).map((attachment, index) => ({ ...attachment, order: attachment.order ?? index }));
    const draft = await this.store.createDraft(this.sessionId, workspacePath, prompt, attachments);
    this.drafts.set(draft.draftId, draft);
    this.setStatus({ state: "draft", reportAvailable: await this.store.getReport(this.sessionId) !== undefined, message: "Codex 草稿等待用户确认。" });
    if (this.onDraft) this.onDraft(draft);
    else {
      // Compatibility fallback for direct controller users that still expose
      // the old approval interaction callback. ManagerSession supplies
      // onDraft, so the desktop path never uses this branch.
      this.onInteraction({
        requestId: draft.draftId,
        kind: "question",
        title: "确认发送 Codex 任务",
        details: `工作区：${workspacePath}\n\n以下是 Antigravity 生成的 Prompt。请检查或编辑后确认：\n\n${prompt}\n\n确认后 ManagerHost 才会启动 Codex。`,
        fields: { type: "object", properties: { answer: { type: "string", description: "用户确认后的 Prompt" } } },
      });
    }
    return draft;
  }

  private async startTask(prompt: string, attachments: ManagerImageAttachment[], statusMessage: string): Promise<string> {
    if (this.activeTask && !this.activeTask.finished) throw new Error("Codex 已有任务正在运行。");
    const taskId = `task-${randomUUID()}`;
    const startedAt = new Date().toISOString();
    try {
      this.cancelRequested = false;
      await this.onTaskStart?.(taskId, startedAt);
      const active: ActiveTask = {
        taskId,
        prompt,
        attachments,
        startedAt,
        startedAtMs: Date.now(),
        events: 0,
        finished: false,
        commands: [],
        tests: [],
        changedFiles: [],
        warnings: [],
        pendingQuestions: [],
        diff: "",
        finalText: "",
        streamedText: "",
      };
      this.activeTask = active;
      await this.store.saveReport(this.buildReport(active, "queued", ""));
      this.setStatus({ state: "queued", taskId, reportAvailable: true, message: statusMessage });
      void this.runTask(active);
      return taskId;
    } catch (error) {
      this.activeTask = undefined;
      this.onTaskFinished?.(taskId, "failed");
      throw error;
    }
  }

  private async runTask(active: ActiveTask): Promise<void> {
    try {
      const bridge = await this.ensureBridge();
      if (active.finished || this.activeTask !== active) return;
      if (this.cancelRequested) {
        await this.finishTask(active, "cancelled", "Codex 任务已取消。", "cancelled");
        return;
      }
      bridge.sendCodeTask(active.taskId, active.prompt, active.attachments);
      this.setStatus({ state: "running", taskId: active.taskId, phase: "working", reportAvailable: true, message: "Codex 正在处理已确认的任务。" });
    } catch (error) {
      const message = error instanceof Error ? error.message : "Codex 启动失败。";
      await this.finishTask(active, "failed", message, "failed");
      const code = errorCode(error);
      if (code === "CODEX_NOT_FOUND" || code === "CODEX_AUTH_REQUIRED") {
        this.setStatus({ state: "unavailable", taskId: active.taskId, reportAvailable: true, message });
      }
    }
  }

  private async ensureBridge(): Promise<CodexAppServerBridge> {
    if (this.bridge) return this.bridge;
    const storedThreadId = await this.store.getThreadId(this.sessionId);
    let bridge = this.newBridge(storedThreadId);
    try {
      await bridge.start();
    } catch (error) {
      await bridge.dispose();
      if (!(storedThreadId && error instanceof CodexWorkerError && error.code === "CODEX_THREAD_RESUME_FAILED")) throw error;
      bridge = this.newBridge(undefined);
      await bridge.start();
    }
    this.bridge = bridge;
    if (bridge.sessionRef) await this.store.setThreadId(this.sessionId, bridge.sessionRef);
    return bridge;
  }

  private newBridge(threadId?: string): CodexAppServerBridge {
    const settings: CodexWorkerSettings = {
      sessionId: this.sessionId,
      workspacePath: this.workspacePath,
      executable: this.executable,
      threadId,
      model: this.model,
      effort: this.effort,
      approvalPolicy: this.approvalPolicy,
      sandboxMode: this.sandboxMode,
      mode: "observation",
    };
    const onEvent = (event: CodingWorkerEvent) => {
      this.eventQueue = this.eventQueue.then(() => this.onBridgeEvent(event)).catch(() => undefined);
    };
    return this.bridgeFactory?.(settings, onEvent) ?? new CodexAppServerBridge(settings, onEvent);
  }

  private async onBridgeEvent(event: CodingWorkerEvent): Promise<void> {
    const active = this.activeTask;
    if (!active || active.finished) return;
    const taskId = String(event.taskId ?? "");
    if (taskId && taskId !== active.taskId) return;
    active.events++;
    await this.store.appendEvent(active.taskId, event);
    const type = String(event.type ?? "");
    if (type === "assistant_delta") {
      // A stream delta is an arbitrary token boundary. Do not trim or
      // collapse whitespace here: a leading space often belongs to the
      // current token and removing it glues words together in the desktop
      // transcript (for example "I'm" + " preparing").
      const text = redactObservationDelta(event.text ?? "", 12_000);
      active.finalText = `${active.finalText}${String(event.text ?? "")}`.slice(-24_000);
      if (text) {
        active.streamedText = appendPrefix(active.streamedText, text, 12_000);
        this.onEvent?.({ type: "assistant_delta", taskId: active.taskId, text });
      }
      return;
    }
    if (type === "thinking_delta") {
      const text = redactObservationDelta(event.text ?? "", 12_000);
      if (text) this.onEvent?.({ type: "thinking_delta", taskId: active.taskId, text });
      return;
    }
    if (type === "tool_output") {
      const text = redactObservationDelta(event.text ?? event.output ?? "", 12_000);
      if (text) this.onEvent?.({ type: "tool_output", taskId: active.taskId, callId: String(event.callId ?? ""), tool: String(event.tool ?? "工具"), text });
      return;
    }
    if (type === "tool_started") {
      active.phase = String(event.tool ?? "working");
      this.onEvent?.({ type: "tool_started", taskId: active.taskId, callId: String(event.callId ?? ""), tool: String(event.tool ?? "工具"), command: redactObservationText(event.command ?? "", 2_000) || undefined });
      this.setStatus({ state: "running", taskId: active.taskId, phase: active.phase, reportAvailable: true });
      return;
    }
    if (type === "tool_completed") {
      this.collectToolResult(active, event);
      this.onEvent?.({
        type: "tool_completed",
        taskId: active.taskId,
        callId: String(event.callId ?? ""),
        tool: String(event.tool ?? "工具"),
        ok: event.ok !== false,
        summary: redactObservationText(event.summary ?? "", 2_000),
        command: redactObservationText(event.command ?? "", 2_000) || undefined,
        output: redactObservationText(event.output ?? "", 12_000) || undefined,
        diff: redactObservationText(event.diff ?? "", 12_000) || undefined,
      });
      return;
    }
    if (type === "interaction_request") {
      const requestId = String(event.requestId ?? "");
      const details = interactionDetails(event);
      if (details && !active.pendingQuestions.includes(details)) active.pendingQuestions.push(redactObservationText(details, 600));
      this.setStatus({ state: "awaiting_user_input", taskId: active.taskId, phase: "awaiting_user_input", reportAvailable: true, message: "Codex 正在等待用户确认或输入。" });
      this.onInteraction({ requestId, kind: String(event.kind ?? "question"), title: String(event.title ?? "Codex 等待用户输入"), details, command: textValue(event.command), diff: textValue(event.diff) || undefined, fields: event.fields, url: textValue(event.url) || undefined });
      return;
    }
    if (type === "interaction_completed") {
      this.setStatus({ state: "running", taskId: active.taskId, phase: active.phase ?? "working", reportAvailable: true });
      return;
    }
    if (type === "error") {
      const message = redactObservationText(event.message ?? event.text ?? "Codex 任务失败。", 1_000);
      if (message && !active.warnings.includes(message)) active.warnings.push(message);
      return;
    }
    if (type === "observation_result") {
      const status = mapObservationStatus(event.status);
      const text = String(event.text ?? active.finalText).trim();
      await this.finishTask(active, status, text, status);
    }
  }

  private collectToolResult(active: ActiveTask, event: CodingWorkerEvent): void {
    const tool = String(event.tool ?? "");
    const summary = redactObservationText(event.summary ?? "", 800);
    const command = redactObservationText(event.command ?? "", 1_200);
    const exitCode = parseExitCode(summary);
    if (tool === "run_command") {
      active.commands.push({ command, exitCode, summary });
      if (isTestCommand(command)) active.tests.push({ name: command, status: event.ok === false ? "failed" : exitCode !== undefined && exitCode !== 0 ? "failed" : "passed", summary });
      if (event.ok === false || (exitCode !== undefined && exitCode !== 0)) active.warnings.push(summary || `命令失败：${command}`);
    }
    if (tool === "modify_files") {
      for (const file of command.split(/\r?\n/).map((item) => item.trim()).filter(Boolean)) {
        if (!active.changedFiles.some((entry) => entry.path === file)) active.changedFiles.push({ path: file });
      }
      if (typeof event.diff === "string" && event.diff) active.diff = `${active.diff}${active.diff ? "\n" : ""}${event.diff}`.slice(-512_000);
    }
    if (summary && event.ok === false && !active.warnings.includes(summary)) active.warnings.push(summary);
  }

  private async finishTask(active: ActiveTask, state: "completed" | "failed" | "cancelled" | "partial", text: string, _sourceStatus: string): Promise<void> {
    if (active.finished && this.activeTask !== active) return;
    active.finished = true;
    const report = this.buildReport(active, state, text);
    report.workspaceFingerprint = await workspaceFingerprint(this.workspacePath);
    await this.store.saveReport(report, active.diff);
    const safeText = redactObservationText(report.summary, 12_000);
    // The desktop already rendered assistant_delta events. Reconcile the
    // terminal report against that exact projected prefix and send only text
    // that was not visible yet. This avoids replaying the whole final answer
    // after a streamed turn, including turns containing multiple message items
    // whose final text is joined with newlines by the App Server.
    const streamed = redactObservationText(active.streamedText, 12_000);
    const suffix = completionSuffix(safeText, streamed);
    this.onEvent?.({ type: "completed", taskId: active.taskId, status: state, text: suffix, startedAt: report.startedAt, completedAt: report.completedAt, durationMs: report.durationMs });
    if (this.activeTask === active) this.activeTask = undefined;
    this.setStatus({ state, taskId: active.taskId, reportAvailable: true, message: report.summary, startedAt: report.startedAt, completedAt: report.completedAt, durationMs: report.durationMs });
    this.onTaskFinished?.(active.taskId, state);
    this.cancelRequested = false;
  }

  private buildReport(active: ActiveTask, state: CodexTaskState, text: string): CodexTaskReport {
    const summary = redactObservationText(text || active.finalText || (state === "completed" ? "Codex 已完成任务，但没有返回额外说明。" : `Codex 任务${state === "cancelled" ? "已取消" : "未完成"}。`), 4_000);
    const completedAt = state === "queued" ? undefined : new Date().toISOString();
    const durationMs = completedAt ? Math.max(0, Date.parse(completedAt) - active.startedAtMs) : undefined;
    return {
      taskId: active.taskId,
      sessionId: this.sessionId,
      state,
      summary,
      changedFiles: active.changedFiles.slice(0, 200),
      commands: active.commands.slice(0, 200),
      tests: active.tests.slice(0, 200),
      warnings: [...new Set(active.warnings)].slice(0, 100),
      pendingQuestions: [...new Set(active.pendingQuestions)].slice(0, 20),
      startedAt: active.startedAt,
      completedAt,
      durationMs,
    };
  }

  private async status(): Promise<CodexObservationStatus> {
    if (this.activeTask && !this.activeTask.finished) return this.currentStatus;
    if (this.currentStatus.state === "unavailable") return this.currentStatus;
    const report = await this.store.getReport(this.sessionId);
    return report
      ? { sessionId: this.sessionId, state: report.state, taskId: report.taskId, reportAvailable: true, message: report.summary }
      : this.currentStatus;
  }

  private async readReport(taskId?: string): Promise<CodexTaskReport | undefined> {
    const report = await this.store.getReport(this.sessionId, taskId);
    if (!report?.workspaceFingerprint) return report;
    const current = await workspaceFingerprint(this.workspacePath);
    if (!current || current === report.workspaceFingerprint) return report;
    return {
      ...report,
      warnings: [...new Set([...report.warnings, "报告生成后工作区已发生变化，以上结果可能已过期。"])].slice(0, 100),
    };
  }

  private setStatus(partial: Omit<CodexObservationStatus, "sessionId">): void {
    const active = this.activeTask;
    this.currentStatus = {
      sessionId: this.sessionId,
      ...partial,
      startedAt: partial.startedAt ?? active?.startedAt ?? this.currentStatus.startedAt,
      completedAt: partial.completedAt ?? this.currentStatus.completedAt,
      durationMs: partial.durationMs ?? (active && !active.finished && active.startedAtMs ? Math.max(0, Date.now() - active.startedAtMs) : this.currentStatus.durationMs),
    };
    this.onStatus?.(this.currentStatus);
  }
}

function defaultObservationRoot(): string {
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  return process.env.ILMATTO_CODEX_OBSERVATION_DIR ?? path.join(localAppData, "IlMatto", "codex-observation");
}

function hashPrompt(prompt: string): string { return createHash("sha256").update(prompt, "utf8").digest("hex"); }

function isSafeFileStem(value: string): boolean { return /^[A-Za-z0-9_-]{1,200}$/.test(value); }

function fail(code: string, message: string): CodexObservationResult { return { ok: false, error: { code, message } }; }

function errorCode(error: unknown): string {
  return error instanceof CodexWorkerError ? error.code : typeof (error as any)?.code === "string" ? (error as any).code : "CODEX_OBSERVATION_ERROR";
}

function mapObservationStatus(value: unknown): "completed" | "failed" | "cancelled" | "partial" {
  const status = String(value ?? "completed").toLowerCase();
  if (status.includes("cancel") || status.includes("interrupt")) return "cancelled";
  if (status.includes("fail") || status.includes("error")) return "failed";
  if (status === "completed" || status === "success" || status === "succeeded") return "completed";
  return "partial";
}

function parseExitCode(summary: string): number | undefined {
  const match = /(?:退出码|exit\s*code|code)\s*[:=]?\s*(-?\d+)/i.exec(summary);
  return match ? Number(match[1]) : undefined;
}

function isTestCommand(command: string): boolean {
  return /(?:\btest\b|\btests\b|pytest|unittest|npm\s+(?:run\s+)?test|dotnet\s+test|cargo\s+test|ctest|cmake\s+--build|compile|build)/i.test(command);
}

function textValue(value: unknown): string {
  if (typeof value === "string") return value.trim();
  if (value === undefined || value === null) return "";
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  try { return JSON.stringify(value, null, 2) ?? ""; } catch { return String(value); }
}

function interactionDetails(event: Record<string, any>): string {
  const direct = textValue(event.details);
  if (direct && direct !== "[object Object]") return direct;
  const fallback = [event.reason, event.command, event.cwd, event.workingDirectory, event.path, event.message, event.prompt]
    .map(textValue).filter(Boolean).join("\n");
  return fallback || "Codex 请求用户确认或输入，但未提供具体内容。";
}

function redactObservationText(value: unknown, maximum: number): string {
  const text = String(value ?? "")
    .replace(/(sk-[A-Za-z0-9_-]{8,}|AIza[\w-]{20,}|Bearer\s+[A-Za-z0-9._~+\/-]{12,})/gi, "[已脱敏]")
    .replace(/((?:api|access|auth|refresh)[_-]?token|password|secret)\s*[=:]\s*[^\s"']+/gi, "$1=[已脱敏]")
    .replace(/\s+/g, " ")
    .trim();
  return text.length <= maximum ? text : `${text.slice(0, Math.max(0, maximum - 1))}…`;
}

/**
 * Redact a streamed delta without changing its boundary whitespace. Deltas
 * must remain lossless from a layout perspective because the App Server may
 * split a sentence immediately before a space or newline.
 */
function redactObservationDelta(value: unknown, maximum: number): string {
  const text = String(value ?? "")
    .replace(/(sk-[A-Za-z0-9_-]{8,}|AIza[\w-]{20,}|Bearer\s+[A-Za-z0-9._~+\/-]{12,})/gi, "[已脱敏]")
    .replace(/((?:api|access|auth|refresh)[_-]?token|password|secret)\s*[=:]\s*[^\s"']+/gi, "$1=[已脱敏]");
  return text.length <= maximum ? text : `${text.slice(0, Math.max(0, maximum - 1))}…`;
}

function appendPrefix(current: string, chunk: string, maximum: number): string {
  if (!chunk || current.length >= maximum) return current;
  const remaining = maximum - current.length;
  return current + chunk.slice(0, remaining);
}

/**
 * Return only the terminal text that is not already represented by the
 * streamed prefix. Matching ignores whitespace because App Server may join
 * separate agent-message items with newlines while deltas arrive without a
 * separator. The original final text is retained for any genuinely new
 * suffix, so Markdown and punctuation are not rewritten.
 */
function completionSuffix(full: string, streamed: string): string {
  const finalText = full.trim();
  const streamedText = streamed.trim();
  if (!streamedText) return finalText;
  if (!finalText) return "";

  let fullIndex = 0;
  let streamedIndex = 0;
  while (streamedIndex < streamedText.length) {
    while (fullIndex < finalText.length && /\s/.test(finalText[fullIndex])) fullIndex++;
    while (streamedIndex < streamedText.length && /\s/.test(streamedText[streamedIndex])) streamedIndex++;
    if (streamedIndex >= streamedText.length) break;
    if (fullIndex >= finalText.length || finalText[fullIndex] !== streamedText[streamedIndex]) {
      // A report truncated to its bounded summary can be wholly covered by
      // the stream even though it is shorter than the displayed text.
      const compactFinal = finalText.replace(/\s+/g, "").replace(/…$/, "");
      const compactStreamed = streamedText.replace(/\s+/g, "");
      if (compactFinal && compactStreamed.startsWith(compactFinal)) return "";
      return finalText;
    }
    fullIndex++;
    streamedIndex++;
  }
  return finalText.slice(fullIndex).trimStart();
}

function redactObservationDetails(event: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(event)) {
    if (["type", "timestamp"].includes(key)) continue;
    if (typeof value === "string") result[key] = redactObservationText(value, key === "output" || key === "diff" ? 12_000 : 3_000);
    else if (typeof value === "number" || typeof value === "boolean" || value === null) result[key] = value;
  }
  return result;
}

function workspaceFingerprint(workspacePath: string): Promise<string | undefined> {
  return new Promise((resolve) => {
    execFile("git", ["status", "--porcelain=v1", "--untracked-files=all"], { cwd: workspacePath, windowsHide: true, timeout: 5_000, maxBuffer: 2_000_000 }, (error, stdout) => {
      if (error) { resolve(undefined); return; }
      resolve(createHash("sha256").update(String(stdout ?? ""), "utf8").digest("hex"));
    });
  });
}
