import net from "node:net";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { mkdir, readFile as readSessionFile, realpath, stat, unlink, writeFile } from "node:fs/promises";
import { Type } from "@mariozechner/pi-ai";
import { AuthStorage, createAgentSession, DefaultResourceLoader, defineTool, ModelRegistry, SessionManager, SettingsManager } from "@mariozechner/pi-coding-agent";
import { applyPatch, listFiles, readFile, runCommand, searchText } from "./tools.js";
import { commitGitChanges, createGitBranch, createInitialGitCommit, getGitBranches, getGitDiff, getGitLog, getGitOverview, getGitRemotes, getStagedSummary, initGitRepository, showGitCommit, stageGitFiles, switchGitBranch, unstageGitFiles } from "./git-service.js";
import { isClientMessage, type AgentSessionMode, type ClientMessage, type CodeResult, type HostMessage, type RestoreTranscriptMessage } from "./protocol.js";
import { normalizeWorkspace } from "./security.js";
import { isGitWriteTool, isSafeCommand } from "./approval-policy.js";
import { formatProviderError, getProviderModelOptions, normalizeProviderBaseUrl } from "./provider-config.js";

const inlinePipe = process.argv.find((arg) => arg.startsWith("--pipe="));
const pipeFlagIndex = process.argv.indexOf("--pipe");
const pipeArgument = inlinePipe?.slice("--pipe=".length) ?? (pipeFlagIndex >= 0 ? process.argv[pipeFlagIndex + 1] : undefined);
if (!pipeArgument) { console.error("Missing --pipe argument"); process.exit(2); }
const pipeName = pipeArgument.startsWith("\\\\.\\pipe\\") ? pipeArgument : `\\\\.\\pipe\\${pipeArgument}`;
const preferredSessionDirectory = path.resolve(process.env.ILMATTO_SESSION_DIR ?? path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"), "IlMatto", "sessions"));
let sessionDirectory = preferredSessionDirectory;

class HostError extends Error {
  constructor(public readonly code: string, message: string) { super(message); }
}

const server = net.createServer((socket) => {
  socket.setEncoding("utf8");
  let buffer = "";
  const send = (message: HostMessage) => socket.write(`${JSON.stringify(message)}\n`);
  send({ type: "host_ready", version: "0.1.0" });
  socket.on("data", (chunk) => {
    buffer += chunk;
    let newline = buffer.indexOf("\n");
    while (newline >= 0) {
      const line = buffer.slice(0, newline).trim(); buffer = buffer.slice(newline + 1); newline = buffer.indexOf("\n");
      if (!line) continue;
      try {
        const parsed = JSON.parse(line);
        if (!isClientMessage(parsed)) throw new Error("Invalid message");
        void handle(parsed, send).catch((error) => send({ type: "error", sessionId: "sessionId" in parsed ? parsed.sessionId : undefined, message: error instanceof Error ? error.message : "Request failed", code: error instanceof HostError ? error.code : "REQUEST_ERROR" }));
      }
      catch (error) { send({ type: "error", message: error instanceof Error ? error.message : "Invalid request", code: "PROTOCOL_ERROR" }); }
    }
  });
});

type Send = (message: HostMessage) => void;
const sessions = new Map<string, AgentSession>();
const sessionStarts = new Map<string, Promise<void>>();
let shuttingDown = false;

async function handle(message: ClientMessage, send: Send): Promise<void> {
  if (message.type === "shutdown") { await shutdownHost(send, message.sessionId); return; }
  if (message.type === "delete_session") {
    const start = sessionStarts.get(message.sessionId);
    if (start) await start;
    const active = sessions.get(message.sessionId);
    const activeSessionFile = active?.sessionFile;
    if (active) { await active.dispose(); sessions.delete(message.sessionId); }
    const sessionFile = await validateSessionPath(message.sessionFile ?? activeSessionFile, false);
    if (sessionFile) {
      try { await unlink(sessionFile); } catch (error: any) {
        if (error?.code !== "ENOENT") throw new HostError("SESSION_DELETE_FAILED", `无法删除会话文件：${error instanceof Error ? error.message : "未知错误"}`);
      }
    }
    send({ type: "session_deleted", sessionId: message.sessionId, sessionFile });
    return;
  }
  if (message.type === "approve_tool_call") {
    const session = sessions.get(message.sessionId);
    if (!session) throw new Error("Session not found");
    if (!session.resolveApproval(message.callId, message.approved))
      send({ type: "error", sessionId: message.sessionId, message: `Approval request not found: ${message.callId}`, code: "APPROVAL_NOT_FOUND" });
    return;
  }
  if (message.type === "start_session" || message.type === "new_session") {
    const workspacePath = normalizeWorkspace(message.workspacePath);
    const baseUrl = normalizeProviderBaseUrl(message.baseUrl);
    if (!/^https?:\/\//i.test(baseUrl)) throw new HostError("INVALID_BASE_URL", "Base URL 必须以 http:// 或 https:// 开头。" );
    const existing = sessions.get(message.sessionId);
    const mode = message.mode ?? "interactive";
    if (existing && message.type === "start_session" && existing.matches(workspacePath, baseUrl, message.modelId, message.apiKey, message.autoApproveSafeCommands ?? false, message.autoApproveGitOperations ?? false, message.sessionFile, mode)) { existing.emitCommands(); await existing.emitGitOverview(); existing.emitReady(); return; }
    if (existing) await existing.dispose();
    const session = new AgentSession(message.sessionId, workspacePath, baseUrl, message.modelId, message.apiKey, message.autoApproveSafeCommands ?? false, message.autoApproveGitOperations ?? false, send, message.sessionFile, message.restoreTranscript, mode);
    sessions.set(message.sessionId, session);
    const start = session.start();
    sessionStarts.set(message.sessionId, start);
    try { await start; }
    catch (error) { if (sessions.get(message.sessionId) === session) sessions.delete(message.sessionId); throw error; }
    finally { sessionStarts.delete(message.sessionId); }
    return;
  }
  const start = sessionStarts.get(message.sessionId);
  if (start) await start;
  const session = sessions.get(message.sessionId);
  if (!session) throw new Error("Session not found");
  if (message.type === "send_message") await session.prompt(message.text);
  if (message.type === "code_task") await session.runCodeTask(message.taskId, message.userRequest);
  if (message.type === "cancel") session.cancel();
  if (message.type === "get_commands") session.emitCommands();
  if (message.type === "get_git_overview") await session.emitGitOverview();
  if (message.type === "get_git_diff") await session.emitGitDiff(message.scope, message.path);
}

const HOST_COMMANDS = [
  ["settings", "Open IlMatto settings"], ["model", "Select the configured model"],
  ["scoped-models", "Enable or disable models for cycling"], ["export", "Export the current session"],
  ["import", "Import a session"], ["share", "Share the session"], ["copy", "Copy the last assistant message"],
  ["name", "Set the session display name"], ["session", "Show session information"], ["changelog", "Show changelog entries"],
  ["hotkeys", "Show keyboard shortcuts"], ["fork", "Create a new fork"], ["clone", "Duplicate the current session"],
  ["tree", "Navigate the session tree"], ["login", "Configure provider authentication"], ["logout", "Remove provider authentication"],
  ["new", "Start a new session"], ["compact", "Compact the session context"], ["resume", "Resume another session"],
  ["reload", "Reload extensions and prompts"], ["quit", "Quit IlMatto"], ["help", "Show available commands"],
  ["commands", "Show available commands"],
] as const;

function isWithinDirectory(candidate: string, directory: string): boolean {
  const resolvedCandidate = path.resolve(candidate).toLowerCase();
  const resolvedDirectory = path.resolve(directory).toLowerCase();
  return resolvedCandidate === resolvedDirectory || resolvedCandidate.startsWith(`${resolvedDirectory}${path.sep}`);
}

async function validateSessionPath(value: string | undefined, requireExisting: boolean): Promise<string | undefined> {
  if (!value) return undefined;
  const resolved = path.resolve(value);
  if (!isWithinDirectory(resolved, sessionDirectory) || path.dirname(resolved).toLowerCase() !== path.resolve(sessionDirectory).toLowerCase() || path.extname(resolved).toLowerCase() !== ".jsonl")
    throw new HostError("SESSION_RESTORE_INVALID", "会话文件必须位于当前用户 AppData\\Local\\IlMatto\\sessions 目录内。" );
  await ensureSessionDirectory();
  try {
    const real = await realpath(resolved);
    if (!isWithinDirectory(real, sessionDirectory)) throw new HostError("SESSION_RESTORE_INVALID", "会话文件符号链接不能指向 sessions 目录之外。" );
  } catch (error: any) {
    if (error instanceof HostError) throw error;
    if (error?.code !== "ENOENT") throw new HostError("SESSION_RESTORE_INVALID", "无法访问会话文件。" );
    if (requireExisting) throw new HostError("SESSION_RESTORE_INVALID", "会话文件不存在。" );
  }
  return resolved;
}

async function ensureSessionDirectory(): Promise<void> {
  try {
    await mkdir(sessionDirectory, { recursive: true });
    // An existing directory can still be read-only (for example after a
    // corporate profile policy change). Probe an actual file write so Pi does
    // not fail later with a vague EPERM while creating the session JSONL.
    const probe = path.join(sessionDirectory, `.write-test-${process.pid}-${Math.random().toString(16).slice(2)}.tmp`);
    await writeFile(probe, "", { flag: "wx" });
    await unlink(probe);
  }
  catch (error: any) {
    if (sessionDirectory !== preferredSessionDirectory || !["EPERM", "EACCES"].includes(error?.code)) throw error;
    sessionDirectory = path.join(os.tmpdir(), "ilmatto-sessions");
    await mkdir(sessionDirectory, { recursive: true });
  }
}

async function inspectSessionFile(filePath: string, workspace: string): Promise<"empty" | "valid"> {
  let content: string;
  try { content = await readSessionFile(filePath, "utf8"); }
  catch (error: any) {
    if (error?.code === "ENOENT") return "empty";
    throw new HostError("SESSION_RESTORE_INVALID", "无法读取会话文件。" );
  }
  const lines = content.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  if (lines.length === 0) return "empty";
  const entries: any[] = [];
  for (const line of lines) {
    try { entries.push(JSON.parse(line)); }
    catch { throw new HostError("SESSION_RESTORE_INVALID", "会话文件包含损坏的 JSONL 数据，未加载该会话。" ); }
  }
  const header = entries.find((entry) => entry?.type === "session");
  if (!header || typeof header.cwd !== "string") throw new HostError("SESSION_RESTORE_INVALID", "会话文件缺少有效的工作区信息，未加载该会话。" );
  if (path.resolve(header.cwd).toLowerCase() !== path.resolve(workspace).toLowerCase())
    throw new HostError("SESSION_WORKSPACE_MISMATCH", `会话工作区与当前工作区不一致：${header.cwd}`);
  return "valid";
}

async function shutdownHost(send: Send, sessionId?: string): Promise<void> {
  if (shuttingDown) return;
  shuttingDown = true;
  send({ type: "session_state", sessionId: sessionId ?? "", state: "cancelled" });
  for (const session of sessions.values()) await session.dispose();
  sessions.clear();
  server.close(() => process.exit(0));
}

async function fileExists(filePath: string): Promise<boolean> {
  try { await stat(filePath); return true; } catch { return false; }
}

function limitTranscript(messages: RestoreTranscriptMessage[]): RestoreTranscriptMessage[] {
  const bounded = messages
    .filter((message) => (message.role === "user" || message.role === "assistant") && typeof message.text === "string" && message.text.trim())
    .slice(-40)
    .map((message) => ({ role: message.role, text: message.text }));
  let remaining = 32_000;
  const result: RestoreTranscriptMessage[] = [];
  for (let index = bounded.length - 1; index >= 0 && remaining > 0; index--) {
    const message = bounded[index];
    const text = message.text.slice(-remaining);
    result.unshift({ role: message.role, text });
    remaining -= text.length;
  }
  return result;
}

function appendTranscript(manager: any, transcript: RestoreTranscriptMessage[], modelId: string): void {
  let estimatedTokens = 0;
  for (const message of transcript) {
    const timestamp = Date.now();
    estimatedTokens += Math.max(1, Math.ceil(message.text.length / 4));
    if (message.role === "user") {
      manager.appendMessage({ role: "user", content: message.text, timestamp });
      continue;
    }
    manager.appendMessage({
      role: "assistant",
      content: [{ type: "text", text: message.text }],
      api: "openai-completions",
      provider: "ilmatto",
      model: modelId,
      usage: {
        input: estimatedTokens, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: estimatedTokens,
        cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
      },
      stopReason: "stop",
      timestamp,
    });
  }
}

class AgentSession {
  private cancelled = false;
  private promptRunning = false;
  private piSession: any;
  private readonly pendingApprovals = new Map<string, (approved: boolean) => void>();
  private readonly autoApprovedCalls = new Set<string>();
  // Keep the preview diff associated with a tool call until its completion
  // event. This is especially important for auto-approved patches, where no
  // approval request is sent to the desktop.
  private readonly callDiffs = new Map<string, string | undefined>();
  private sessionManager: any;
  private settingsManager: any;
  private restored = false;
  private legacyRestored = false;
  private restoreError?: HostError;
  private lastCacheRead = 0;
  private lastCacheWrite = 0;
  private thinkingDeltaReceived = false;
  private activeTaskId?: string;
  private submittedTaskId?: string;
  private commands: Array<{ name: string; description?: string; source: string }> = HOST_COMMANDS.map(([name, description]) => ({ name, description, source: "extension" }));
  constructor(
    private readonly id: string,
    private readonly workspace: string,
    private readonly baseUrl: string,
    private readonly modelId: string,
    private readonly apiKey: string | undefined,
    private readonly autoApproveSafeCommands: boolean,
    private readonly autoApproveGitOperations: boolean,
    private readonly send: Send,
    private readonly requestedSessionFile?: string,
    private readonly restoreTranscript?: RestoreTranscriptMessage[],
    private readonly mode: AgentSessionMode = "interactive",
  ) {}
  async start(): Promise<void> {
    const sessionManager = await this.prepareSessionManager();
    this.sessionManager = sessionManager;
    this.settingsManager = SettingsManager.inMemory({ compaction: { enabled: true, reserveTokens: 16_384, keepRecentTokens: 20_000 } });
    const authStorage = AuthStorage.inMemory();
    if (this.apiKey) authStorage.setRuntimeApiKey("ilmatto", this.apiKey);
    const modelRegistry = ModelRegistry.inMemory(authStorage);
    const providerOptions = getProviderModelOptions(this.baseUrl);
    modelRegistry.registerProvider("ilmatto", {
      name: "IlMatto OpenAI-compatible",
      baseUrl: this.baseUrl,
      api: "openai-completions",
      apiKey: "runtime",
      models: [{ id: this.modelId, name: this.modelId, reasoning: providerOptions.reasoning, input: ["text"], contextWindow: providerOptions.contextWindow, maxTokens: providerOptions.maxTokens, compat: providerOptions.compat, headers: providerOptions.headers, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } }],
    });
    const model = modelRegistry.find("ilmatto", this.modelId);
    if (!model) throw new Error(`Model not found: ${this.modelId}`);
    const resourceLoader = new DefaultResourceLoader({
      cwd: this.workspace,
      agentDir: path.join(os.tmpdir(), "ilmatto-agent-resources"),
      settingsManager: this.settingsManager,
      systemPrompt: [
        "You are IlMatto, a cautious coding agent.",
        ...(this.mode === "coding_worker" ? [
          "You are operating as the sole coding specialist behind an external policy broker. You own all repository analysis, architecture choices, implementation decisions, testing, and Git decisions.",
          "Do not defer technical decisions to the coordinator. Work directly from the user's original request and the inspected repository.",
          "Every coding task MUST finish by calling submit_code_result exactly once. Ordinary assistant prose is not a valid completion result.",
          "Use status blocked only when a concrete user product decision or missing authority prevents safe progress, and include the exact questions to ask.",
        ] : []),
        "Use only the provided workspace tools. Inspect before changing files.",
        "File changes and PowerShell commands require explicit user approval.",
        "Use the dedicated Git tools for repository inspection and local Git changes; never invoke Git through PowerShell.",
        "Git file status, diffs, staging, and commits are scoped to the selected workspace. Creating or switching a branch is repository-wide and requires the selected workspace to be the repository root.",
        "If Git status reports that the workspace is not a repository and version control would help, call git_init to initialize the selected workspace. git_init is intentionally auto-approved, stages the current workspace files, and creates a local Initial commit when there are files to commit; it never contacts a remote. If the workspace is inside an ancestor repository, use that repository instead of initializing a nested one.",
        "Before a Git change, inspect status and the relevant diff. Before committing, inspect staged files and summarize the commit. Do not include unstaged files in a commit.",
        "Never attempt remote Git operations, destructive Git commands, or Git configuration changes. Explain branch-switch conflicts and missing Git identity instead of working around them.",
        "After an approved change, run an appropriate read-only check or test when useful, then summarize.",
      ].join("\n"),
      extensionFactories: [
        (pi) => {
          for (const [name, description] of HOST_COMMANDS) {
            pi.registerCommand(name, {
              description,
              handler: async (args, ctx) => this.handleCommand(name, args, ctx),
            });
          }
        },
      ],
    });
    await resourceLoader.reload();
    const customTools = createPiTools(this);
    const created = await createAgentSession({
      cwd: this.workspace,
      authStorage,
      modelRegistry,
      model,
      resourceLoader,
      sessionManager,
      settingsManager: this.settingsManager,
      tools: customTools.map((tool: any) => tool.name),
      customTools,
    });
    this.piSession = created.session;
    this.piSession.setAutoCompactionEnabled?.(true);
    // Pi intentionally delays creating an empty session file until the first
    // assistant response. IlMatto needs the path to be durable immediately so
    // a command-only turn can still be reopened without losing its identity.
    try { (this.sessionManager as any)._rewriteFile?.(); } catch { }
    const runtimeCommands = created.extensionsResult.runtime.getCommands?.() ?? [];
    this.commands = [...this.commands, ...runtimeCommands
      .filter((command: any) => !this.commands.some((known) => known.name === command.name))
      .map((command: any) => ({ name: command.name, description: command.description, source: command.source ?? "extension" }))];
    const activeToolNames = this.piSession.getActiveToolNames?.() ?? [];
    if (activeToolNames.length !== customTools.length) throw new Error(`Tool configuration mismatch: expected ${customTools.length}, got ${activeToolNames.length}`);
    this.piSession.subscribe((event: any) => {
      if (event?.type === "message_update" && event.assistantMessageEvent?.type === "text_delta") this.emitDelta(event.assistantMessageEvent.delta);
      if (event?.type === "message_update" && event.assistantMessageEvent?.type === "thinking_delta") this.emitThinking(event.assistantMessageEvent.delta);
      if (event?.type === "message_update" && event.assistantMessageEvent?.type === "thinking_end" && !this.thinkingDeltaReceived) this.emitThinking(event.assistantMessageEvent.content);
      if (event?.type === "compaction_start") this.onCompactionStart(event);
      if (event?.type === "compaction_end") this.onCompactionEnd(event);
    });
    await this.autoInitializeGitIfNeeded();
    this.emitCommands();
    this.emitReady();
    this.emitMetrics();
    this.send({ type: "session_state", sessionId: this.id, state: "idle" });
    await this.emitGitOverview();
  }
  async prompt(text: string): Promise<void> {
    if (this.mode === "coding_worker") {
      this.send({ type: "error", sessionId: this.id, message: "coding_worker sessions only accept code_task messages", code: "WORKER_PROTOCOL_ERROR" });
      return;
    }
    await this.runPrompt(text);
  }
  async runCodeTask(taskId: string, userRequest: string): Promise<void> {
    if (this.mode !== "coding_worker") {
      this.send({ type: "error", sessionId: this.id, message: "code_task requires a coding_worker session", code: "WORKER_PROTOCOL_ERROR" });
      return;
    }
    this.activeTaskId = taskId;
    this.submittedTaskId = undefined;
    await this.runPrompt(userRequest);
    if (this.submittedTaskId !== taskId && !this.cancelled) {
      const result: CodeResult = {
        status: "failed",
        summaryForUser: "Coding Worker 已结束，但没有提交结构化结果。",
        technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false,
      };
      this.send({ type: "code_result", sessionId: this.id, taskId, result });
      this.send({ type: "error", sessionId: this.id, message: "Pi ended without calling submit_code_result", code: "WORKER_RESULT_MISSING" });
    }
    this.activeTaskId = undefined;
  }
  private async runPrompt(text: string): Promise<void> {
    if (this.promptRunning) { this.send({ type: "error", sessionId: this.id, message: "A prompt is already running", code: "BUSY" }); return; }
    this.promptRunning = true; this.cancelled = false; this.thinkingDeltaReceived = false; this.send({ type: "session_state", sessionId: this.id, state: "thinking" });
    try {
      if (!this.piSession) throw new Error("Agent session is not initialized");
      await this.piSession.prompt(text);
      if (!this.cancelled) this.send({ type: "assistant_completed", sessionId: this.id, text: "" });
      this.emitMetrics();
      this.send({ type: "session_state", sessionId: this.id, state: this.cancelled ? "cancelled" : "idle" });
    } catch (error) { this.send({ type: "error", sessionId: this.id, message: formatProviderError(error, this.baseUrl, this.modelId), code: "AGENT_ERROR" }); this.send({ type: "session_state", sessionId: this.id, state: "error" }); }
    finally { this.promptRunning = false; }
  }
  cancel(): void {
    this.cancelled = true;
    try { this.piSession?.abort?.(); } catch { }
    try { this.piSession?.abortCompaction?.(); } catch { }
    for (const resolve of this.pendingApprovals.values()) resolve(false);
    this.pendingApprovals.clear();
  }
  async dispose(): Promise<void> {
    this.cancel();
    try { await this.piSession?.abort?.(); } catch { }
    const deadline = Date.now() + 2_000;
    while (this.promptRunning && Date.now() < deadline) await new Promise((resolve) => setTimeout(resolve, 25));
    try { this.piSession?.dispose?.(); } catch { }
  }
  get metadata(): { id: string; workspace: string; baseUrl: string; modelId: string; apiKey?: string } { return { id: this.id, workspace: this.workspace, baseUrl: this.baseUrl, modelId: this.modelId, apiKey: this.apiKey }; }
  get sessionFile(): string | undefined { return this.piSession?.sessionFile ?? this.sessionManager?.getSessionFile?.(); }
  matches(workspace: string, baseUrl: string, modelId: string, apiKey?: string, autoApproveSafeCommands = false, autoApproveGitOperations = false, sessionFile?: string, mode: AgentSessionMode = "interactive"): boolean { const requested = sessionFile ? path.resolve(sessionFile).toLowerCase() : undefined; const active = this.piSession?.sessionFile ? path.resolve(this.piSession.sessionFile).toLowerCase() : undefined; return this.workspace === workspace && this.baseUrl === baseUrl && this.modelId === modelId && this.apiKey === apiKey && this.autoApproveSafeCommands === autoApproveSafeCommands && this.autoApproveGitOperations === autoApproveGitOperations && this.mode === mode && (!requested || requested === active); }
  emitDelta(text: string): void { if (!this.cancelled) this.send({ type: "assistant_delta", sessionId: this.id, text }); }
  emitThinking(text: string): void { if (!this.cancelled && text) { this.thinkingDeltaReceived = true; this.send({ type: "thinking_delta", sessionId: this.id, text }); } }
  emitReady(): void {
    const sessionFile = this.piSession?.sessionFile ?? this.sessionManager?.getSessionFile?.();
    this.send({ type: "session_ready", sessionId: this.id, sessionFile, restored: this.restored, legacyRestored: this.legacyRestored, workspacePath: this.workspace });
    if (this.restoreError) {
      this.send({ type: "error", sessionId: this.id, message: this.restoreError.message, code: this.restoreError.code });
      this.restoreError = undefined;
    }
  }
  private async prepareSessionManager(): Promise<any> {
    await ensureSessionDirectory();
    const requested = await validateSessionPath(this.requestedSessionFile, false);
    const requestedExists = Boolean(requested && await fileExists(requested));
    if (requested && requestedExists) {
      try {
        await inspectSessionFile(requested, this.workspace);
        const manager = SessionManager.open(requested, sessionDirectory, this.workspace);
        this.restored = manager.buildSessionContext().messages.length > 0;
        return manager;
      } catch (error) {
        if (error instanceof HostError && error.code === "SESSION_WORKSPACE_MISMATCH") throw error;
        if (error instanceof HostError && error.code === "SESSION_RESTORE_INVALID") {
          this.restoreError = error;
          return SessionManager.create(this.workspace, sessionDirectory);
        }
        throw new HostError("SESSION_RESTORE_INVALID", error instanceof Error ? error.message : "无法恢复会话文件。" );
      }
    }
    const manager = requested ? SessionManager.open(requested, sessionDirectory, this.workspace) : SessionManager.create(this.workspace, sessionDirectory);
    if (!requestedExists && this.restoreTranscript?.length) {
      const transcript = limitTranscript(this.restoreTranscript);
      if (transcript.length) {
        appendTranscript(manager, transcript, this.modelId);
        this.legacyRestored = true;
        this.restored = true;
      }
    }
    return manager;
  }
  private onCompactionStart(event: any): void {
    this.send({ type: "context_compaction_start", sessionId: this.id, reason: event.reason ?? "threshold", tokensBefore: this.piSession?.getContextUsage?.()?.tokens ?? undefined });
    this.send({ type: "session_state", sessionId: this.id, state: "compacting" });
  }
  private onCompactionEnd(event: any): void {
    const result = event.result;
    this.send({ type: "context_compaction_end", sessionId: this.id, reason: event.reason ?? "threshold", summary: result?.summary, tokensBefore: result?.tokensBefore, aborted: Boolean(event.aborted), willRetry: Boolean(event.willRetry), errorMessage: event.errorMessage });
    if (event.errorMessage) this.send({ type: "error", sessionId: this.id, message: event.errorMessage, code: "COMPACTION_FAILED" });
    this.emitMetrics();
  }
  emitMetrics(): void {
    if (!this.piSession) return;
    try {
      const stats = this.piSession.getSessionStats?.();
      const usage = stats?.contextUsage;
      const cacheReadTotal = Number(stats?.tokens?.cacheRead ?? 0);
      const cacheWriteTotal = Number(stats?.tokens?.cacheWrite ?? 0);
      const cacheStatsAvailable = cacheReadTotal > 0 || cacheWriteTotal > 0;
      const cacheReadTokens = Math.max(0, cacheReadTotal - this.lastCacheRead);
      const cacheWriteTokens = Math.max(0, cacheWriteTotal - this.lastCacheWrite);
      this.lastCacheRead = cacheReadTotal;
      this.lastCacheWrite = cacheWriteTotal;
      this.send({ type: "session_metrics", sessionId: this.id, contextTokens: typeof usage?.tokens === "number" ? usage.tokens : undefined, contextWindow: typeof usage?.contextWindow === "number" ? usage.contextWindow : undefined, contextPercent: typeof usage?.percent === "number" ? usage.percent : undefined, cacheReadTokens, cacheWriteTokens, cacheStatsAvailable });
    } catch { }
  }
  private async autoInitializeGitIfNeeded(): Promise<void> {
    const overview = await getGitOverview(this.workspace);
    const workspaceIsRepositoryRoot = Boolean(overview.root && path.resolve(overview.root).toLowerCase() === path.resolve(this.workspace).toLowerCase());
    const needsInitialization = !overview.isRepository && Boolean(overview.message?.toLowerCase().includes("not inside a git repository"));
    const needsInitialCommit = overview.isRepository && workspaceIsRepositoryRoot && overview.commits.length === 0;
    if (!needsInitialization && !needsInitialCommit) return;
    const callId = `auto-git-init-${this.id}`;
    this.autoApprovedCalls.add(callId);
    this.toolStarted(callId, "git_init", `git init ${this.workspace}`);
    try {
      const output = needsInitialization ? await initGitRepository(this.workspace) : await createInitialGitCommit(this.workspace);
      this.toolCompleted(callId, "git_init", true, needsInitialization ? "Git 已自动初始化并创建初始提交" : "Git 初始提交已自动完成", output);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Git 初始化失败";
      this.toolCompleted(callId, "git_init", false, message, message);
    }
  }
  emitCommands(): void { this.send({ type: "slash_commands", sessionId: this.id, commands: this.commands }); }
  private async handleCommand(name: string, args: string, ctx: any): Promise<void> {
    if (name === "compact") {
      try { await this.piSession.compact(args.trim() || undefined); this.emitCommandResult(name, "当前会话上下文已压缩。"); }
      catch (error) { this.emitCommandResult(name, `上下文压缩失败：${error instanceof Error ? error.message : "未知错误"}`); }
      return;
    }
    if (name === "reload") { await ctx.reload(); this.emitCommands(); this.emitCommandResult(name, "已重新加载扩展、技能和提示模板。"); return; }
    if (name === "help" || name === "commands") { this.emitCommandResult(name, this.commands.map((command) => `/${command.name} — ${command.description ?? ""}`).join("\n")); return; }
    if (name === "session") { this.emitCommandResult(name, `会话 ${this.id}\n工作区：${this.workspace}`); return; }
    if (name === "name") { this.emitCommandResult(name, args.trim() ? `会话名称参数：${args.trim()}` : "请在 /name 后提供名称；桌面端会话标题会根据首条消息生成。"); return; }
    this.emitCommandResult(name, `/${name} 已识别。该命令的交互式 UI 尚未在 IlMatto 中开放，请使用设置、历史会话和侧栏操作。`);
  }
  emitCommandResult(command: string, message: string): void { this.send({ type: "command_result", sessionId: this.id, command, message }); }
  submitCodeResult(callId: string, value: CodeResult): void {
    if (this.mode !== "coding_worker" || !this.activeTaskId) throw new Error("No coding task is active");
    if (this.submittedTaskId === this.activeTaskId) throw new Error("submit_code_result may only be called once per task");
    const result = normalizeCodeResult(value);
    this.submittedTaskId = this.activeTaskId;
    this.send({ type: "code_result", sessionId: this.id, taskId: this.activeTaskId, result });
    this.toolStarted(callId, "submit_code_result", "submit_code_result");
    this.toolCompleted(callId, "submit_code_result", true, "结构化代码结果已提交");
  }
  get isCodingWorker(): boolean { return this.mode === "coding_worker"; }
  toolStarted(callId: string, tool: string, command?: string): void {
    if (this.mode === "coding_worker" && this.activeTaskId && this.submittedTaskId === this.activeTaskId && tool !== "submit_code_result")
      throw new Error("The coding task is already complete; no further tools may run after submit_code_result");
    this.send({ type: "tool_started", sessionId: this.id, callId, tool, command });
  }
  toolOutput(callId: string, tool: string, text: string): void { if (text) this.send({ type: "tool_output", sessionId: this.id, callId, tool, text }); }
  toolCompleted(callId: string, tool: string, ok: boolean, summary: string, output?: string): void {
    const autoApproved = this.autoApprovedCalls.delete(callId);
    const diff = this.callDiffs.get(callId);
    this.callDiffs.delete(callId);
    this.send({ type: "tool_completed", sessionId: this.id, callId, tool, ok, summary, output, diff, autoApproved: autoApproved || undefined });
  }
  resolveApproval(callId: string, approved: boolean): boolean {
    const resolve = this.pendingApprovals.get(callId);
    if (!resolve) return false;
    this.pendingApprovals.delete(callId);
    resolve(approved);
    return true;
  }
  async approve(callId: string, request: { tool: string; summary: string; details: string; diff?: string; command?: string }): Promise<boolean> {
    if (request.diff) this.callDiffs.set(callId, request.diff);
    if (this.autoApproveSafeCommands && request.tool === "run_command" && isSafeCommand(request.command ?? "")) return true;
    if (request.tool === "git_init") { this.autoApprovedCalls.add(callId); return true; }
    if (this.autoApproveGitOperations && isGitWriteTool(request.tool)) { this.autoApprovedCalls.add(callId); return true; }
    return new Promise((resolve) => {
      // Register before notifying the desktop so an immediate response can
      // never arrive before the approval waiter exists.
      this.pendingApprovals.set(callId, resolve);
      this.send({ type: "tool_approval_request", sessionId: this.id, callId, tool: request.tool, summary: request.summary, details: request.details, diff: request.diff });
      this.send({ type: "session_state", sessionId: this.id, state: "waiting_approval" });
    });
  }
  async emitGitOverview(): Promise<void> { this.send({ type: "git_overview", sessionId: this.id, overview: await getGitOverview(this.workspace) }); }
  async emitGitDiff(scope: "working" | "staged", relativePath?: string): Promise<void> {
    try {
      const result = await getGitDiff(this.workspace, scope, relativePath);
      this.send({ type: "git_diff", sessionId: this.id, scope: result.scope, path: result.path, content: result.content, truncated: result.truncated });
    } catch (error) {
      this.send({ type: "git_diff", sessionId: this.id, scope, path: relativePath, content: error instanceof Error ? error.message : "Unable to read Git diff", truncated: false });
    }
  }
}


function createPiTools(session: AgentSession): any[] {
  const executeReadOnly = async (callId: string, tool: string, command: string, action: () => Promise<string>): Promise<any> => {
    session.toolStarted(callId, tool, command);
    try { const output = await action(); session.toolCompleted(callId, tool, true, "只读操作完成", output); return { content: [{ type: "text", text: output }], details: { output } }; }
    catch (error) { const message = error instanceof Error ? error.message : "Tool failed"; session.toolCompleted(callId, tool, false, message); return { content: [{ type: "text", text: message }], details: { error: message }, isError: true }; }
  };
  const executeApproval = async (callId: string, tool: string, command: string, action: () => Promise<string>): Promise<any> => {
    session.toolStarted(callId, tool, command);
    try { const output = await action(); session.toolCompleted(callId, tool, !output.startsWith("User denied"), output, output); return { content: [{ type: "text", text: output }], details: { output } }; }
    catch (error) { const message = error instanceof Error ? error.message : "Tool failed"; const output = typeof (error as any)?.output === "string" ? (error as any).output : undefined; session.toolCompleted(callId, tool, false, message, output); return { content: [{ type: "text", text: output ? `${message}\n${output}` : message }], details: { error: message, output }, isError: true }; }
  };
  const executeGitWrite = async (callId: string, tool: string, command: string, summary: string, details: string, action: () => Promise<string>): Promise<any> => executeApproval(callId, tool, command, async () => {
    const approved = await session.approve(callId, { tool, summary, details, command });
    if (!approved) return "User denied the Git operation.";
    const output = await action();
    await session.emitGitOverview();
    return output;
  });
  const tools = [
    defineTool({ name: "list_files", label: "List files", description: "List files and directories in the selected workspace.", parameters: Type.Object({ relativePath: Type.Optional(Type.String()) }), async execute(callId: string, params: any) { const relativePath = params.relativePath ?? "."; return executeReadOnly(callId, "list_files", `list_files ${relativePath}`, () => listFiles(session.metadata.workspace, relativePath)); } }),
    defineTool({ name: "read_file", label: "Read file", description: "Read a UTF-8 text file inside the selected workspace.", parameters: Type.Object({ relativePath: Type.String() }), async execute(callId: string, params: any) { return executeReadOnly(callId, "read_file", `read_file ${params.relativePath}`, () => readFile(session.metadata.workspace, params.relativePath)); } }),
    defineTool({ name: "search_text", label: "Search text", description: "Search text with ripgrep inside the selected workspace.", parameters: Type.Object({ query: Type.String() }), async execute(callId: string, params: any) { return executeReadOnly(callId, "search_text", `search_text ${params.query}`, () => searchText(session.metadata.workspace, params.query)); } }),
    defineTool({ name: "git_init", label: "Initialize Git repository", description: "Initialize a local Git repository, stage the current workspace, and create an Initial commit when files exist. This never contacts a remote.", parameters: Type.Object({}), async execute(callId: string) { return executeApproval(callId, "git_init", `git init ${session.metadata.workspace}`, async () => { const approved = await session.approve(callId, { tool: "git_init", summary: "Initialize Git repository and create initial commit", details: `This will create a .git directory and include all current files in the selected workspace in an Initial commit:\n${session.metadata.workspace}\n\nNo remote will be contacted.`, command: `git init ${session.metadata.workspace}` }); if (!approved) return "User denied Git initialization."; const output = await initGitRepository(session.metadata.workspace); await session.emitGitOverview(); return output; }); } }),
    defineTool({ name: "git_status", label: "Git status", description: "Show the selected repository's branch, changes, tracking state, and recent commits.", parameters: Type.Object({}), async execute(callId: string) { return executeReadOnly(callId, "git_status", "git status --porcelain=v1 --branch --untracked-files=all", async () => formatGitOverview(await getGitOverview(session.metadata.workspace))); } }),
    defineTool({ name: "git_diff", label: "Git diff", description: "Show a working-tree or staged Git diff, optionally for one path relative to the selected workspace.", parameters: Type.Object({ scope: Type.Optional(Type.Union([Type.Literal("working"), Type.Literal("staged")])), relativePath: Type.Optional(Type.String()) }), async execute(callId: string, params: any) { const scope = params.scope === "staged" ? "staged" : "working"; return executeReadOnly(callId, "git_diff", `git diff ${scope}${params.relativePath ? ` -- ${params.relativePath}` : ""}`, async () => (await getGitDiff(session.metadata.workspace, scope, params.relativePath)).content || "No changes in this diff."); } }),
    defineTool({ name: "git_log", label: "Git log", description: "Show up to 25 recent local commits. This never contacts remotes.", parameters: Type.Object({}), async execute(callId: string) { return executeReadOnly(callId, "git_log", "git log -25 --oneline", () => getGitLog(session.metadata.workspace)); } }),
    defineTool({ name: "git_branches", label: "Git branches", description: "Show local branches and their tracking configuration.", parameters: Type.Object({}), async execute(callId: string) { return executeReadOnly(callId, "git_branches", "git branch --verbose", () => getGitBranches(session.metadata.workspace)); } }),
    defineTool({ name: "git_show", label: "Git show", description: "Show one local commit by revision, including its summary and diff statistics.", parameters: Type.Object({ revision: Type.String() }), async execute(callId: string, params: any) { return executeReadOnly(callId, "git_show", `git show ${params.revision}`, () => showGitCommit(session.metadata.workspace, params.revision)); } }),
    defineTool({ name: "git_remotes", label: "Git remotes", description: "Show locally configured remote names and URLs without making network requests.", parameters: Type.Object({}), async execute(callId: string) { return executeReadOnly(callId, "git_remotes", "git remote -v", () => getGitRemotes(session.metadata.workspace)); } }),
    defineTool({ name: "git_stage", label: "Git stage", description: "Stage specified paths relative to the selected workspace after approval.", parameters: Type.Object({ paths: Type.Array(Type.String()) }), async execute(callId: string, params: any) { const paths = params.paths as string[]; return executeGitWrite(callId, "git_stage", `git add -- ${paths.join(" ")}`, "Stage Git changes", `Files to stage:\n${paths.join("\n")}`, () => stageGitFiles(session.metadata.workspace, paths)); } }),
    defineTool({ name: "git_unstage", label: "Git unstage", description: "Remove selected-workspace paths from the Git staging area after approval; working files are not changed.", parameters: Type.Object({ paths: Type.Array(Type.String()) }), async execute(callId: string, params: any) { const paths = params.paths as string[]; return executeGitWrite(callId, "git_unstage", `git restore --staged -- ${paths.join(" ")}`, "Unstage Git changes", `Files to unstage (working files stay unchanged):\n${paths.join("\n")}`, () => unstageGitFiles(session.metadata.workspace, paths)); } }),
    defineTool({ name: "git_create_branch", label: "Create Git branch", description: "Create and switch to a new local branch after approval. No remote branch is created.", parameters: Type.Object({ name: Type.String() }), async execute(callId: string, params: any) { return executeGitWrite(callId, "git_create_branch", `git switch -c ${params.name}`, "Create and switch Git branch", `New local branch: ${params.name}`, () => createGitBranch(session.metadata.workspace, params.name)); } }),
    defineTool({ name: "git_switch_branch", label: "Switch Git branch", description: "Switch to an existing local branch after approval. It will not stash or overwrite worktree changes.", parameters: Type.Object({ name: Type.String() }), async execute(callId: string, params: any) { return executeGitWrite(callId, "git_switch_branch", `git switch ${params.name}`, "Switch Git branch", `Target local branch: ${params.name}\nGit will refuse this if local changes would be overwritten.`, () => switchGitBranch(session.metadata.workspace, params.name)); } }),
    defineTool({ name: "git_commit", label: "Commit staged Git changes", description: "Create a local commit from already staged changes after approval. Hooks are not run and no remote is contacted.", parameters: Type.Object({ message: Type.String() }), async execute(callId: string, params: any) { const staged = await getStagedSummary(session.metadata.workspace); return executeGitWrite(callId, "git_commit", "git commit --no-verify", "Create local Git commit", `Commit message:\n${params.message}\n\nStaged files:\n${staged}`, () => commitGitChanges(session.metadata.workspace, params.message)); } }),
    defineTool({ name: "apply_patch", label: "Apply patch", description: "Replace a text file after displaying a diff and receiving user approval.", parameters: Type.Object({ relativePath: Type.String(), content: Type.String() }), async execute(callId: string, params: any) { return executeApproval(callId, "apply_patch", `apply_patch ${params.relativePath}`, () => applyPatch(session.metadata.workspace, params.relativePath, params.content, (request) => session.approve(callId, request))); } }),
    defineTool({ name: "run_command", label: "Run PowerShell", description: "Run a non-interactive PowerShell command in the selected workspace after user approval.", parameters: Type.Object({ command: Type.String() }), async execute(callId: string, params: any) { return executeApproval(callId, "run_command", params.command, () => runCommand(session.metadata.workspace, params.command, (request) => session.approve(callId, request), (chunk) => session.toolOutput(callId, "run_command", chunk))); } }),
  ];
  if (session.isCodingWorker) {
    tools.push(defineTool({
      name: "submit_code_result",
      label: "Submit code result",
      description: "Submit the mandatory structured result for the active coding task. Call exactly once after work is complete or blocked.",
      parameters: Type.Object({
        status: Type.Union([Type.Literal("completed"), Type.Literal("blocked"), Type.Literal("failed"), Type.Literal("cancelled")]),
        summaryForUser: Type.String(),
        technicalDecisions: Type.Array(Type.Object({ decision: Type.String(), reason: Type.String() })),
        filesChanged: Type.Array(Type.Object({ path: Type.String(), additions: Type.Integer({ minimum: 0 }), deletions: Type.Integer({ minimum: 0 }) })),
        validation: Type.Array(Type.Object({ command: Type.String(), status: Type.Union([Type.Literal("passed"), Type.Literal("failed"), Type.Literal("skipped")]), summary: Type.String() })),
        questions: Type.Array(Type.String()),
        needsUserDecision: Type.Boolean(),
      }),
      async execute(callId: string, params: any) {
        try {
          session.submitCodeResult(callId, params as CodeResult);
          return { content: [{ type: "text", text: "Structured coding result submitted." }], details: { submitted: true, error: undefined as string | undefined } };
        } catch (error) {
          const message = error instanceof Error ? error.message : "Unable to submit code result";
          return { content: [{ type: "text", text: message }], details: { submitted: false, error: message }, isError: true };
        }
      },
    }));
  }
  return tools;
}

function normalizeCodeResult(value: CodeResult): CodeResult {
  const statuses = new Set(["completed", "blocked", "failed", "cancelled"]);
  const validationStatuses = new Set(["passed", "failed", "skipped"]);
  if (!value || !statuses.has(value.status) || typeof value.summaryForUser !== "string") throw new Error("Invalid CodeResult");
  return {
    status: value.status,
    summaryForUser: value.summaryForUser,
    technicalDecisions: Array.isArray(value.technicalDecisions) ? value.technicalDecisions.map((item) => ({ decision: String(item.decision ?? ""), reason: String(item.reason ?? "") })) : [],
    filesChanged: Array.isArray(value.filesChanged) ? value.filesChanged.map((item) => ({ path: String(item.path ?? ""), additions: Math.max(0, Math.trunc(Number(item.additions) || 0)), deletions: Math.max(0, Math.trunc(Number(item.deletions) || 0)) })) : [],
    validation: Array.isArray(value.validation) ? value.validation.map((item) => ({ command: String(item.command ?? ""), status: validationStatuses.has(item.status) ? item.status : "skipped", summary: String(item.summary ?? "") })) : [],
    questions: Array.isArray(value.questions) ? value.questions.map(String) : [],
    needsUserDecision: Boolean(value.needsUserDecision),
  };
}

function formatGitOverview(overview: Awaited<ReturnType<typeof getGitOverview>>): string {
  if (!overview.isRepository) return overview.message ?? "当前工作区不在 Git 仓库中";
  const lines = [
    `Repository: ${overview.root}`,
    `Branch: ${overview.branch}${overview.upstream ? ` -> ${overview.upstream}` : ""}`,
    `Tracking: ahead ${overview.ahead}, behind ${overview.behind}`,
    `Staged (${overview.staged.length}): ${overview.staged.map((item) => `${item.status} ${item.path}`).join(", ") || "none"}`,
    `Unstaged (${overview.unstaged.length}): ${overview.unstaged.map((item) => `${item.status} ${item.path}`).join(", ") || "none"}`,
    `Untracked (${overview.untracked.length}): ${overview.untracked.map((item) => item.path).join(", ") || "none"}`,
  ];
  if (overview.remotes.length) lines.push(`Configured remotes (local metadata only): ${overview.remotes.map((remote) => `${remote.name} ${remote.fetchUrl ?? remote.pushUrl ?? ""}`).join(", ")}`);
  return lines.join("\n");
}

server.listen(pipeName);
