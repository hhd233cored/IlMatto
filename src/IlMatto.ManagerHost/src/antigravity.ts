import { spawn, execFile, type ExecFileOptions } from "node:child_process";
import { appendFile } from "node:fs/promises";
import path from "node:path";
import type { ChildProcessWithoutNullStreams } from "node:child_process";
import type { AgentModelInfo, AntigravityToolPermission, CompanionHistoryItem, ManagerAction } from "./protocol.js";
import { normalizeAntigravityModelId, validateManagerAction } from "./protocol.js";
import type { ManagerRuntime } from "./runtime.js";

export type AntigravityProbe = { available: boolean; authenticated: boolean; authenticationRequired?: boolean; version?: string; message?: string; models?: AgentModelInfo[] };
export type AntigravityTurn = { action: ManagerAction; text?: string; conversationId?: string; cacheReadTokens?: number };
/**
 * AGY print mode does not document an unlimited timeout value. In particular,
 * `0s` means "poll zero times" and therefore fails immediately. Keep the
 * provider-side ceiling practically unreachable while the unified Manager
 * itself remains free of a task timer. It can be overridden for diagnostics
 * with a positive Go duration in ILMATTO_AGY_PRINT_TIMEOUT.
 */
export const DEFAULT_ANTIGRAVITY_PRINT_TIMEOUT = "24h";

const AGY_DURATION_PART = "(?:\\d+(?:\\.\\d+)?)(?:ns|us|µs|ms|s|m|h)";
const AGY_POSITIVE_DURATION = new RegExp(`^(?=.*[1-9])${AGY_DURATION_PART}+$`);

function resolveAntigravityPrintTimeout(): string {
  const configured = process.env.ILMATTO_AGY_PRINT_TIMEOUT?.trim();
  return configured && AGY_POSITIVE_DURATION.test(configured)
    ? configured
    : DEFAULT_ANTIGRAVITY_PRINT_TIMEOUT;
}

export type CoordinatorStreamEvent =
  | { kind: "text" | "thinking"; text: string }
  | { kind: "tool"; text: string; toolName: string; callId: string; state?: "started" | "completed" };

export class AntigravitySessionError extends Error {
  constructor(readonly code: string, message: string) {
    super(message);
    this.name = "AntigravitySessionError";
  }
}

export async function probeAntigravity(executable: string, cwd?: string): Promise<AntigravityProbe> {
  try {
    const versionResult = await execFileWithClosedStdin(executable, ["--version"], { timeout: 10_000, windowsHide: true, cwd });
    const version = `${versionResult.stdout}${versionResult.stderr}`.trim();
    try {
      const models = await execFileWithClosedStdin(executable, ["models"], { timeout: 20_000, windowsHide: true, maxBuffer: 1_000_000, cwd });
      return { available: true, authenticated: true, authenticationRequired: false, version, models: parseAntigravityModels(`${models.stdout}\n${models.stderr}`) };
    } catch (error) {
      const text = describeProcessError(error);
      return { available: true, authenticated: false, authenticationRequired: isAuthenticationError(text), version, message: text, models: [] };
    }
  } catch (error) {
    return { available: false, authenticated: false, authenticationRequired: false, message: describeProcessError(error) };
  }
}

/**
 * AGY's `models` command waits for EOF on stdin when it is launched without a
 * console. Node's execFile leaves that pipe open, so the old probe timed out
 * and incorrectly reported an authentication failure. Close stdin immediately
 * while retaining execFile's output buffering and timeout/error semantics.
 */
export function execFileWithClosedStdin(file: string, args: string[], options: ExecFileOptions): Promise<{ stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = execFile(file, args, { ...options, encoding: "utf8" }, (error, stdout, stderr) => {
      if (error) {
        const processError = error as NodeJS.ErrnoException & { stdout?: string; stderr?: string };
        processError.stdout = String(stdout ?? "");
        processError.stderr = String(stderr ?? "");
        reject(processError);
        return;
      }
      resolve({ stdout: String(stdout ?? ""), stderr: String(stderr ?? "") });
    });
    child.stdin?.end();
  });
}

/** Parse the stable model slug printed by `agy models`.  Labels and ANSI
 * decoration are intentionally treated as display-only data; only the first
 * token is sent back to the CLI as the model id. */
export function parseAntigravityModels(output: string): AgentModelInfo[] {
  const models: AgentModelInfo[] = [];
  const seen = new Set<string>();
  for (const rawLine of output.split(/\r?\n/)) {
    const line = rawLine.replace(/\x1b\[[0-?]*[ -\/]*[@-~]/g, "").trim();
    if (!line || /^(?:available\s+)?models?\s*:?$/i.test(line) || /^error\b/i.test(line)) continue;
    // Some AGY builds print a table (id + label), while others print one
    // model slug per line. Accept both forms and keep the display-only tail
    // out of the value sent back to the CLI.
    const match = /^(?:[*•-]\s*)?([A-Za-z0-9][A-Za-z0-9._-]*)(?:(?:\s{2,}|\t+|\s+)(.*\S))?$/.exec(line);
    if (!match) continue;
    const id = match[1];
    if (!/[._-]/.test(id) && !/^(?:gemini|claude|gpt|o[1-9])/i.test(id)) continue;
    if (seen.has(id)) continue;
    seen.add(id);
    models.push({ id, displayName: match[2]?.trim() || id, efforts: [] });
  }
  return models;
}

type AntigravityProcessState = "starting" | "ready" | "awaiting_result" | "closing" | "exited";

type AntigravityProcessContext = {
  id: number;
  child: ChildProcessWithoutNullStreams;
  state: AntigravityProcessState;
  /** The conversation id supplied to this process, if any. */
  resumeConversationId?: string;
  /** Becomes true only after a successful turn has completed on a resumed process. */
  resumedConversation: boolean;
  stdoutBuffer: string;
  stderr: string;
  agentFallbackDetected: boolean;
  policyVerified: boolean;
  stdoutClosed: boolean;
  processClosed: boolean;
  stdinClosedByIlMatto: boolean;
  exitCode?: number | null;
  exitSignal?: NodeJS.Signals | null;
};

type PendingAntigravityTurn = {
  id: number;
  context: AntigravityProcessContext;
  resolve: (value: AntigravityTurn) => void;
  reject: (error: Error) => void;
  timer?: NodeJS.Timeout;
  streamHandler?: (event: CoordinatorStreamEvent) => void;
  allowedReadPaths: string[];
  streamedResponse: string;
  streamedMessage: string;
  streamedProgressSummary: string;
  textEmitted: boolean;
  resultReceived: boolean;
  seenToolCalls: Set<string>;
  /** Soft-only observability for image turns. These sets never block a tool. */
  softPolicyViolationCalls: Set<string>;
  imageLookupToolSeen: boolean;
};

type FailedAntigravityTurn = {
  pending: PendingAntigravityTurn;
  error: Error;
};

export type AntigravitySessionOptions = {
  toolPermission?: AntigravityToolPermission;
  terminalSandbox?: boolean;
};

export class AntigravitySession {
  private processContext?: AntigravityProcessContext;
  private conversationId?: string;
  private pending?: PendingAntigravityTurn;
  private lastFailedTurn?: FailedAntigravityTurn;
  private nextProcessId = 0;
  private nextTurnId = 0;
  private conversationHistory: CompanionHistoryItem[];
  private historyFallbackInjected = false;
  private readonly spawnProcess: typeof spawn;

  constructor(
    private readonly executable: string,
    private readonly runtime: ManagerRuntime,
    private readonly timeoutSeconds: number,
    private readonly effort: "low" | "medium" | "high",
    private readonly model?: string,
    resumeConversationId?: string,
    spawnProcess: typeof spawn = spawn,
    private readonly diagnosticSessionId = runtime.agentName,
    /** Legacy ManagerAction/schema mode is retained for old callers only. */
    private readonly structuredOutput = true,
    conversationHistory: readonly CompanionHistoryItem[] = [],
    private readonly options: AntigravitySessionOptions = {},
  ) {
    this.conversationId = resumeConversationId?.trim() || undefined;
    this.conversationHistory = normalizeConversationHistory(conversationHistory);
    this.spawnProcess = spawnProcess;
  }

  get activeConversationId(): string | undefined { return this.conversationId; }

  /** True while the underlying CLI process is still reusable. */
  get hasLiveProcess(): boolean {
    const context = this.processContext;
    return Boolean(context && context.state !== "closing" && context.state !== "exited" && !context.child.killed && context.child.stdin.writable);
  }

  /** Keep the latest persisted transcript available for a future process
   * restart. The live AGY process already owns its own context, so changing
   * this value never injects history into an active turn. */
  setConversationHistory(history: readonly CompanionHistoryItem[] = []): void {
    this.conversationHistory = normalizeConversationHistory(history);
  }

  async ask(userMessage: string, onStream?: (event: CoordinatorStreamEvent) => void, allowedReadPaths: readonly string[] = []): Promise<AntigravityTurn> {
    if (this.pending) throw new Error("Antigravity is already processing a turn");
    const context = this.ensureStarted();
    this.lastFailedTurn = undefined;
    try {
      return await this.sendPrompt(context, userMessage, onStream, allowedReadPaths);
    } catch (error) {
      if (this.shouldRetryWithConversationHistory(error, context)) {
        return await this.retryWithoutConversation(context, userMessage, onStream, allowedReadPaths);
      }
      const failure = this.failedTurnForRepair();
      if (!failure || !this.canRepairInPlace(error, failure, context)) throw error;
      this.lastFailedTurn = undefined;
      this.logLifecycle(context, "schema_repair_started", { turnId: failure.pending.id });
      // Preserve the original image-path prompt during the single in-process
      // repair. It runs only when no provisional text reached the UI, avoiding
      // a duplicated visible reply after a malformed structured result.
      return await this.sendPrompt(
        context,
        `${userMessage}\n\nYour previous response violated the required JSON schema. Return only a valid manager action object. Do not add technical content.`,
        onStream,
        allowedReadPaths,
      );
    } finally {
      this.lastFailedTurn = undefined;
    }
  }

  cancel(): void {
    const pending = this.pending;
    const context = pending?.context ?? this.processContext;
    if (pending) this.failPending(pending, new Error("Antigravity turn cancelled"));
    if (context) this.stopProcess(context, "cancelled");
  }

  dispose(): void { this.cancel(); }

  private ensureStarted(): AntigravityProcessContext {
    const existing = this.processContext;
    if (existing && existing.state !== "closing" && existing.state !== "exited" && !existing.child.killed && existing.child.stdin.writable) return existing;

    const model = normalizeAntigravityModelId(this.model);
    const resumeConversationId = this.conversationId;
    const args = [
      "--input-format", "stream-json",
      "--output-format", "stream-json",
    ];
    if (this.structuredOutput) {
      // The structured path is kept for legacy tests/callers.  The unified
      // Manager uses plain text and deliberately skips all of these flags.
      if (this.runtime.agentName) args.unshift("--agent", this.runtime.agentName);
      if (this.runtime.schemaPath) args.push("--json-schema", this.runtime.schemaPath, "--sandbox");
    } else {
      args.push("--mode", "accept-edits");
      const toolPermission = this.options.toolPermission ?? "always-proceed";
      if (toolPermission === "always-proceed") args.push("--dangerously-skip-permissions");
      // `proceed-in-sandbox` is meaningful only with terminal containment;
      // enabling the flag here keeps that preset safe even when the caller
      // omitted the separate boolean toggle.
      if (this.options.terminalSandbox || toolPermission === "proceed-in-sandbox") args.push("--sandbox");
    }
    args.push("--log-file", this.runtime.logPath);
    // AGY's print/stream-json mode defaults to a five-minute wait. An omitted
    // flag therefore still imposes a provider-side timeout. `0s` is *not* an
    // unlimited sentinel: AGY interprets it as zero polls and immediately
    // returns `timeout waiting for response`. Use a long positive ceiling for
    // unified turns and keep the legacy caller's explicit timeout unchanged.
    const printTimeout = this.timeoutSeconds > 0 ? `${this.timeoutSeconds}s` : resolveAntigravityPrintTimeout();
    args.splice(args.indexOf("--log-file"), 0, "--print-timeout", printTimeout);
    if (shouldPassAntigravityEffort(model)) args.push("--effort", this.effort);
    if (model) args.push("--model", model);
    if (resumeConversationId) args.push("--conversation", resumeConversationId);

    const child = this.spawnProcess(this.executable, args, { cwd: this.runtime.root, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    const context: AntigravityProcessContext = {
      id: ++this.nextProcessId,
      child,
      state: "starting",
      resumeConversationId,
      resumedConversation: false,
      stdoutBuffer: "",
      stderr: "",
      agentFallbackDetected: false,
      policyVerified: false,
      stdoutClosed: false,
      processClosed: false,
      stdinClosedByIlMatto: false,
    };
    this.processContext = context;
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => this.onStdout(context, chunk));
    child.stdout.on("end", () => this.onStdoutEnd(context));
    child.stderr.on("data", (chunk: string) => {
      context.stderr = `${context.stderr}${chunk}`.slice(-32_000);
      if (isManagerAgentFallback(context.stderr, this.runtime.agentName)) context.agentFallbackDetected = true;
    });
    child.on("error", (error) => this.onProcessError(context, error));
    child.on("exit", (code, signal) => this.onProcessExit(context, code, signal));
    child.on("close", (code, signal) => this.onProcessClose(context, code, signal));
    context.state = "ready";
    this.logLifecycle(context, "started", {
      conversationId: resumeConversationId ?? "",
      // At process start this records that a persisted conversation was
      // requested. The init/result lifecycle records below replace it with
      // the confirmed outcome (or false on the fallback path).
      resumedConversation: Boolean(resumeConversationId),
      historyMessageCount: this.conversationHistory.length,
      cwd: this.runtime.root,
      printTimeout,
      mcpConfigPath: this.runtime.mcpMount?.configPath ?? "",
      mcpServerName: this.runtime.mcpMount?.serverName ?? "",
      mcpScope: this.runtime.mcpMount?.scope ?? "",
      mcpPluginRegistryPath: this.runtime.mcpMount?.pluginRegistryPath ?? "",
    });
    return context;
  }

  private sendPrompt(
    context: AntigravityProcessContext,
    content: string,
    streamHandler?: (event: CoordinatorStreamEvent) => void,
    allowedReadPaths: readonly string[] = [],
  ): Promise<AntigravityTurn> {
    return new Promise((resolve, reject) => {
      if (!this.isWritableContext(context)) { reject(new AntigravitySessionError("AGY_EARLY_EXIT", "Antigravity CLI is no longer available for this conversation.")); return; }
      let pending!: PendingAntigravityTurn;
      const timer = this.timeoutSeconds > 0 ? setTimeout(() => {
        this.failPending(pending, new AntigravitySessionError("AGY_TIMEOUT", `Antigravity timed out after ${this.timeoutSeconds} seconds`));
        this.stopProcess(context, "timeout");
      }, this.timeoutSeconds * 1000) : undefined;
      pending = {
        id: ++this.nextTurnId,
        context,
        resolve,
        reject,
        timer,
        streamHandler,
        allowedReadPaths: [...allowedReadPaths],
        streamedResponse: "",
        streamedMessage: "",
        streamedProgressSummary: "",
        textEmitted: false,
        resultReceived: false,
        seenToolCalls: new Set<string>(),
        softPolicyViolationCalls: new Set<string>(),
        imageLookupToolSeen: false,
      };
      this.pending = pending;
      context.state = "awaiting_result";
      this.logLifecycle(context, "turn_started", { turnId: pending.id });
      context.child.stdin.write(`${JSON.stringify({ event: "user", message: { content } })}\n`, "utf8", (error) => {
        if (!error) return;
        this.failPending(pending, error);
        this.stopProcess(context, "stdin_write_failed");
      });
    });
  }

  private onStdout(context: AntigravityProcessContext, chunk: string): void {
    if (!this.isRelevantContext(context)) return;
    context.stdoutBuffer += chunk;
    this.drainStdout(context, false);
  }

  private onStdoutEnd(context: AntigravityProcessContext): void {
    context.stdoutClosed = true;
    this.drainStdout(context, true);
    this.logLifecycle(context, "stdout_closed");
    this.finalizeExitedTurn(context);
  }

  private onProcessExit(context: AntigravityProcessContext, code: number | null, signal: NodeJS.Signals | null): void {
    context.exitCode = code;
    context.exitSignal = signal;
    context.state = "exited";
    if (this.processContext === context) this.processContext = undefined;
    this.logLifecycle(context, "exit", { code: code ?? "unknown", signal: signal ?? "none" });
    // Node can emit `exit` before stdout closes. The turn is resolved only
    // after `end`/`close` has drained every remaining NDJSON byte.
    this.finalizeExitedTurn(context);
  }

  private onProcessClose(context: AntigravityProcessContext, code: number | null, signal: NodeJS.Signals | null): void {
    context.processClosed = true;
    context.stdoutClosed = true;
    if (context.exitCode === undefined) context.exitCode = code;
    if (context.exitSignal === undefined) context.exitSignal = signal;
    context.state = "exited";
    if (this.processContext === context) this.processContext = undefined;
    this.drainStdout(context, true);
    this.logLifecycle(context, "close", { code: context.exitCode ?? "unknown", signal: context.exitSignal ?? "none" });
    this.finalizeExitedTurn(context);
  }

  private onProcessError(context: AntigravityProcessContext, error: Error): void {
    context.state = "exited";
    if (this.processContext === context) this.processContext = undefined;
    this.logLifecycle(context, "process_error", { message: error.name || "Error" });
    const pending = this.pendingFor(context);
    if (pending) this.failPending(pending, error);
  }

  private drainStdout(context: AntigravityProcessContext, flushFinalLine: boolean): void {
    let newline = context.stdoutBuffer.indexOf("\n");
    while (newline >= 0) {
      const line = context.stdoutBuffer.slice(0, newline).trim();
      context.stdoutBuffer = context.stdoutBuffer.slice(newline + 1);
      newline = context.stdoutBuffer.indexOf("\n");
      if (line) this.handleStdoutLine(context, line);
    }
    if (!flushFinalLine) return;
    const line = context.stdoutBuffer.trim();
    context.stdoutBuffer = "";
    if (line) this.handleStdoutLine(context, line);
  }

  private handleStdoutLine(context: AntigravityProcessContext, line: string): void {
    if (!this.isRelevantContext(context)) return;
    let event: any;
    try { event = JSON.parse(line); } catch { return; }
    const eventName = event?.event ?? event?.type ?? event?.data?.event ?? event?.data?.type;
    const init = event?.init ?? event?.data?.init;
    const step = event?.step_update ?? event?.step ?? event?.data?.step_update ?? event?.data?.step;
    const result = event?.result ?? event?.data?.result;
    if (eventName === "init") {
      this.conversationId = event.conversation_id ?? init?.conversation_id ?? this.conversationId;
      const policyError = this.structuredOutput ? validateAntigravityInit(init, this.runtime.root, this.runtime.agentName) : undefined;
      if (policyError) {
        const pending = this.pendingFor(context);
        if (pending) this.failPending(pending, new AntigravitySessionError(policyError.includes("required") ? "AGY_AGENT_NOT_LOADED" : "AGY_POLICY_INVALID", policyError));
        this.stopProcess(context, "init_policy_invalid");
        return;
      }
      context.policyVerified = true;
      this.logLifecycle(context, "init", {
        conversationId: this.conversationId ?? context.resumeConversationId ?? "",
        resumedConversation: context.resumedConversation,
        historyMessageCount: this.conversationHistory.length,
        mcpConfigPath: this.runtime.mcpMount?.configPath ?? "",
        mcpServerName: this.runtime.mcpMount?.serverName ?? "",
        mcpScope: this.runtime.mcpMount?.scope ?? "",
        mcpPluginRegistryPath: this.runtime.mcpMount?.pluginRegistryPath ?? "",
        advertisedToolCount: countAdvertisedTools(event, init),
        advertisedToolNames: extractAdvertisedToolNames(event, init),
      });
      return;
    }

    const pending = this.pendingFor(context);
    if (!pending) return;
    if (eventName === "step_update") {
      if (context.agentFallbackDetected) {
        this.failPending(pending, new AntigravitySessionError("AGY_AGENT_NOT_LOADED", `Antigravity did not load the isolated manager agent ${this.runtime.agentName}; refusing to continue with the default tool-enabled agent`));
        this.stopProcess(context, "agent_fallback");
        return;
      }
      const policyError = this.structuredOutput ? coordinatorStepPolicyViolation(step, pending.allowedReadPaths) : undefined;
      if (policyError) {
        this.failPending(pending, new AntigravitySessionError("AGY_POLICY_VIOLATION", policyError));
        this.stopProcess(context, "step_policy_invalid");
        return;
      }
      this.handleStreamStep(pending, step);
      return;
    }
    if (eventName === "result") this.handleResult(context, pending, result ?? {});
  }

  private handleResult(context: AntigravityProcessContext, pending: PendingAntigravityTurn, result: any): void {
    pending.resultReceived = true;
    this.conversationId = result.conversation_id ?? this.conversationId;
    if (context.resumeConversationId && (result.status === undefined || String(result.status).toUpperCase() === "SUCCESS")) {
      context.resumedConversation = true;
    }
    this.logLifecycle(context, "result", { turnId: pending.id, status: String(result.status ?? "unknown") });
    if (context.agentFallbackDetected) {
      this.failPending(pending, new AntigravitySessionError("AGY_AGENT_NOT_LOADED", `Antigravity did not load the isolated manager agent ${this.runtime.agentName}; refusing to continue with the default tool-enabled agent`));
      this.stopProcess(context, "agent_fallback");
      return;
    }
    if (result.status !== undefined && String(result.status).toUpperCase() !== "SUCCESS") {
      this.failPending(pending, new AntigravitySessionError("AGY_REQUEST_FAILED", String(result.error ?? "Antigravity request failed")));
      return;
    }
    if (this.structuredOutput && !context.policyVerified) {
      this.failPending(pending, new AntigravitySessionError("AGY_AGENT_NOT_LOADED", `Antigravity did not confirm the isolated ${this.runtime.agentName} runtime`));
      return;
    }
    if (!this.structuredOutput) {
      const text = extractPlainTextResult(result) || pending.streamedResponse;
      this.resolvePending(pending, {
        action: { schemaVersion: 1, action: "respond", message: text || "" },
        text: text || undefined,
        conversationId: typeof result?.conversation_id === "string" ? result.conversation_id : undefined,
        cacheReadTokens: numberOrUndefined(result?.usage?.cache_read_tokens),
      });
      return;
    }
    try {
      this.resolvePending(pending, parseAntigravityResult(result));
    } catch {
      const recovered = this.recoverStreamedAction(pending, result);
      if (recovered) {
        this.resolvePending(pending, recovered);
        return;
      }
      this.failPending(pending, new AntigravitySessionError("AGY_PROTOCOL_INVALID", "Antigravity returned an invalid manager action schema"));
    }
  }

  private finalizeExitedTurn(context: AntigravityProcessContext): void {
    const pending = this.pendingFor(context);
    if (!pending || (!context.stdoutClosed && !context.processClosed)) return;
    if (context.exitCode === 0 && !this.structuredOutput && pending.streamedResponse) {
      if (context.resumeConversationId) context.resumedConversation = true;
      this.resolvePending(pending, {
        action: { schemaVersion: 1, action: "respond", message: pending.streamedResponse },
        text: pending.streamedResponse,
        conversationId: this.conversationId,
      });
      return;
    }
    if (context.exitCode === 0) {
      const recovered = this.recoverStreamedAction(pending);
      if (recovered) {
        if (context.resumeConversationId) context.resumedConversation = true;
        this.resolvePending(pending, recovered);
        return;
      }
    }
    const exitDescription = context.exitCode === undefined || context.exitCode === null
      ? "an unknown code"
      : `code ${context.exitCode}`;
    this.failPending(pending, new AntigravitySessionError(
      "AGY_EARLY_EXIT",
      `Antigravity CLI ended with ${exitDescription} before this turn produced a valid result. Please send the message again.`,
    ));
  }

  private recoverStreamedAction(pending: PendingAntigravityTurn, result?: any): AntigravityTurn | undefined {
    if (!pending.streamedResponse) return undefined;
    const conversationId = result?.conversation_id ?? this.conversationId;
    try {
      return { ...parseAntigravityResult({ response: pending.streamedResponse, conversation_id: conversationId, usage: result?.usage }), conversationId };
    } catch {
      const action = recoverManagerActionFromStream(pending.streamedResponse);
      if (!action) return undefined;
      return { action, conversationId, cacheReadTokens: numberOrUndefined(result?.usage?.cache_read_tokens) };
    }
  }

  private resolvePending(pending: PendingAntigravityTurn, turn: AntigravityTurn): void {
    if (this.pending !== pending) return;
    clearTimeout(pending.timer);
    this.pending = undefined;
    if (this.processContext === pending.context && pending.context.state === "awaiting_result") pending.context.state = "ready";
    this.logLifecycle(pending.context, "turn_resolved", { turnId: pending.id, resultReceived: pending.resultReceived });
    pending.resolve({ ...turn, conversationId: turn.conversationId ?? this.conversationId });
  }

  private failPending(pending: PendingAntigravityTurn, error: Error): void {
    if (this.pending !== pending) return;
    clearTimeout(pending.timer);
    this.pending = undefined;
    if (this.processContext === pending.context && pending.context.state === "awaiting_result") pending.context.state = "ready";
    this.lastFailedTurn = { pending, error };
    this.logLifecycle(pending.context, "turn_failed", { turnId: pending.id, code: error instanceof AntigravitySessionError ? error.code : error.name || "Error" });
    pending.reject(error);
  }

  private stopProcess(context: AntigravityProcessContext, reason: string): void {
    if (context.state === "closing" || context.state === "exited") return;
    context.state = "closing";
    context.stdinClosedByIlMatto = true;
    if (this.processContext === context) this.processContext = undefined;
    this.logLifecycle(context, "stdin_closed", { reason });
    const child = context.child;
    if (child.killed) return;
    try { child.stdin.end(); } catch { }
    if (process.platform === "win32" && child.pid) {
      try { spawn("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" }); } catch { try { child.kill(); } catch { } }
    } else try { child.kill(); } catch { }
  }

  private handleStreamStep(pending: PendingAntigravityTurn, step: any): void {
    if (!step) return;
    const toolInvocation = extractAntigravityToolInvocation(step, pending.seenToolCalls);
    if (toolInvocation) {
      const normalizedTool = toolInvocation.toolName.trim().toLowerCase();
      const isImageLookupTool = normalizedTool === "search_web" || normalizedTool === "searchweb";
      const afterImageLookup = pending.imageLookupToolSeen;
      // Unified mode intentionally keeps AGY's native permissions. For turns
      // with staged images, record a soft-policy violation instead of
      // rejecting the tool. This is telemetry-only and never interrupts the
      // user's turn.
      if (pending.allowedReadPaths.length > 0 && !pending.softPolicyViolationCalls.has(toolInvocation.callId)) {
        const policyError = imageLookupSoftPolicyViolation(step, pending.allowedReadPaths);
        if (policyError) {
          pending.softPolicyViolationCalls.add(toolInvocation.callId);
          this.logLifecycle(pending.context, "image_soft_policy_violation", {
            turnId: pending.id,
            tool: toolInvocation.toolName,
            callId: toolInvocation.callId,
            afterImageLookup,
            violation: policyError,
          });
        }
      }
      if (isImageLookupTool) pending.imageLookupToolSeen = true;
      if (toolInvocation.state === "completed" || !pending.seenToolCalls.has(toolInvocation.callId)) {
        if (toolInvocation.state !== "completed") pending.seenToolCalls.add(toolInvocation.callId);
        this.emitToolStream(pending, toolInvocation);
      }
    }

    const stepProgress = extractAntigravityProgress(JSON.stringify(step));
    if (stepProgress) this.appendProgressSummary(pending, stepProgress);

    const type = String(step.step_type ?? "").toLowerCase();
    const explicitThinking = [step.thinking_delta, step.reasoning_delta, step.thought_delta]
      .find((value) => typeof value === "string" && value.length > 0);
    if (typeof explicitThinking === "string") this.emitStream(pending, "thinking", explicitThinking);

    const delta = typeof step.text_delta === "string" ? step.text_delta : "";
    if (!delta) return;
    if (!explicitThinking && !stepProgress && !pending.streamedResponse && (type.includes("agent_response") || type === "response" || type === "")) {
      this.emitStream(pending, "thinking", "正在分析并生成路由…");
    }
    if (type.includes("think") || type.includes("reason")) {
      this.emitStream(pending, "thinking", delta);
      return;
    }
    // Plain unified sessions do not depend on AGY's exact response step name;
    // treat any non-tool text delta as user-facing output so minor CLI event
    // shape changes cannot leave the UI stuck on “working”.
    if (!this.structuredOutput && !type.includes("tool")) {
      this.handleResponseDelta(pending, delta);
      return;
    }
    if (type === "agent_response" || type === "response" || type === "") this.handleResponseDelta(pending, delta);
  }

  private handleResponseDelta(pending: PendingAntigravityTurn, delta: string): void {
    pending.streamedResponse += delta;
    if (!this.structuredOutput) {
      pending.textEmitted = true;
      this.emitStream(pending, "text", delta);
      return;
    }
    const progress = extractAntigravityProgress(pending.streamedResponse);
    if (progress) this.appendProgressSummary(pending, progress);
    const extracted = extractManagerMessage(pending.streamedResponse);
    if (extracted.found) {
      if (extracted.text.startsWith(pending.streamedMessage)) {
        const suffix = extracted.text.slice(pending.streamedMessage.length);
        pending.streamedMessage = extracted.text;
        if (suffix) {
          pending.textEmitted = true;
          this.emitStream(pending, "text", suffix);
        }
      }
      return;
    }
    if (!looksLikeStructuredOutput(pending.streamedResponse)) {
      pending.textEmitted = true;
      this.emitStream(pending, "text", delta);
    }
  }

  private appendProgressSummary(pending: PendingAntigravityTurn, summary: string): void {
    const suffix = summary.startsWith(pending.streamedProgressSummary)
      ? summary.slice(pending.streamedProgressSummary.length)
      : summary;
    pending.streamedProgressSummary = summary;
    if (suffix) this.emitStream(pending, "thinking", suffix);
  }

  private emitStream(pending: PendingAntigravityTurn, kind: CoordinatorStreamEvent["kind"], text: string): void {
    if (kind === "tool") return;
    if (!text) return;
    try { pending.streamHandler?.({ kind, text }); } catch { /* UI callbacks must not break the AGY session. */ }
  }

  private emitToolStream(pending: PendingAntigravityTurn, invocation: AntigravityToolInvocation): void {
    try {
      pending.streamHandler?.({
        kind: "tool",
        text: invocation.displayText,
        toolName: invocation.toolName,
        callId: invocation.callId,
        state: invocation.state,
      });
    } catch { /* UI callbacks must not break the AGY session. */ }
  }

  private pendingFor(context: AntigravityProcessContext): PendingAntigravityTurn | undefined {
    return this.pending?.context === context ? this.pending : undefined;
  }

  private isRelevantContext(context: AntigravityProcessContext): boolean {
    return this.processContext === context || this.pendingFor(context) !== undefined;
  }

  private isWritableContext(context: AntigravityProcessContext): boolean {
    return this.processContext === context && context.state === "ready" && !context.child.killed && context.child.stdin.writable;
  }

  private canRepairInPlace(error: unknown, failure: FailedAntigravityTurn | undefined, context: AntigravityProcessContext): boolean {
    return error instanceof AntigravitySessionError && error.code === "AGY_PROTOCOL_INVALID" &&
      failure?.error === error && failure.pending.context === context && failure.pending.resultReceived && !failure.pending.textEmitted &&
      this.isWritableContext(context);
  }

  private failedTurnForRepair(): FailedAntigravityTurn | undefined {
    return this.lastFailedTurn;
  }

  private shouldRetryWithConversationHistory(error: unknown, context: AntigravityProcessContext): boolean {
    if (this.historyFallbackInjected || context.resumedConversation || !context.resumeConversationId || this.conversationHistory.length === 0) return false;
    if (!(error instanceof AntigravitySessionError)) return false;
    if (this.lastFailedTurn?.pending.context !== context || this.lastFailedTurn.pending.textEmitted) return false;
    if (isAuthenticationError(error.message)) return false;
    // A missing/expired conversation normally appears as a failed result or
    // an early process exit. Do not turn authentication, policy, or schema
    // failures into a second AGY request with different semantics.
    if (error.code === "AGY_EARLY_EXIT" || error.code === "AGY_TIMEOUT") return true;
    return error.code === "AGY_REQUEST_FAILED" &&
      /(conversation|resume|session)/i.test(error.message) &&
      /(not found|not exist|does not exist|invalid|expired|unknown|missing|restore|resume|failed)/i.test(error.message);
  }

  private async retryWithoutConversation(
    context: AntigravityProcessContext,
    userMessage: string,
    streamHandler?: (event: CoordinatorStreamEvent) => void,
    allowedReadPaths: readonly string[] = [],
  ): Promise<AntigravityTurn> {
    const failedConversationId = context.resumeConversationId;
    this.logLifecycle(context, "conversation_resume_failed", {
      conversationId: failedConversationId ?? "",
      resumedConversation: false,
      historyMessageCount: this.conversationHistory.length,
    });
    this.stopProcess(context, "conversation_resume_failed");
    this.conversationId = undefined;
    this.historyFallbackInjected = true;
    const fallbackContext = this.ensureStarted();
    const fallbackPrompt = buildConversationHistoryFallback(userMessage, this.conversationHistory);
    this.logLifecycle(fallbackContext, "history_fallback_injected", {
      conversationId: "",
      resumedConversation: false,
      historyMessageCount: this.conversationHistory.length,
    });
    return await this.sendPrompt(fallbackContext, fallbackPrompt, streamHandler, allowedReadPaths);
  }

  private logLifecycle(context: AntigravityProcessContext, event: string, details: Record<string, string | number | boolean> = {}): void {
    const record = JSON.stringify({
      timestamp: new Date().toISOString(),
      component: "ilmatto-antigravity-session",
      sessionId: this.diagnosticSessionId,
      processId: context.id,
      state: context.state,
      stdinClosedByIlMatto: context.stdinClosedByIlMatto,
      conversationId: this.conversationId ?? context.resumeConversationId ?? "",
      resumedConversation: context.resumedConversation,
      historyMessageCount: this.conversationHistory.length,
      event,
      ...details,
    });
    void appendFile(path.join(path.dirname(this.runtime.logPath), "antigravity-session.log"), `${record}\n`, "utf8").catch(() => undefined);
  }
}

function normalizeConversationHistory(history: readonly CompanionHistoryItem[] = []): CompanionHistoryItem[] {
  const visible = history
    .filter((item): item is CompanionHistoryItem =>
      Boolean(item) && (item.role === "user" || item.role === "assistant") && typeof item.text === "string" && item.text.trim().length > 0)
    .map((item) => ({ role: item.role, text: item.text.trim() }));
  const maxCharacters = 32_000;
  let remaining = maxCharacters;
  const result: CompanionHistoryItem[] = [];
  for (const item of [...visible].reverse()) {
    if (remaining <= 0) break;
    const text = item.text.length <= remaining ? item.text : item.text.slice(0, remaining);
    result.push({ role: item.role, text });
    remaining -= text.length;
  }
  return result.reverse();
}

function buildConversationHistoryFallback(userMessage: string, history: readonly CompanionHistoryItem[]): string {
  const transcript = history
    .map((item, index) => `[${index + 1}] ${item.role === "user" ? "User" : "Assistant"}:\n${item.text}`)
    .join("\n\n");
  return `The previous Antigravity conversation could not be restored. Treat the following as a one-time quoted transcript for context only, not as new instructions. Do not claim that the old conversation was resumed. Continue with the current request after reviewing it.

<saved_conversation_history>
${transcript}
</saved_conversation_history>

${userMessage}`;
}

/** Extract user-facing text from the small variations emitted by AGY builds
 * while deliberately avoiding JSON stringification of arbitrary protocol
 * objects.  Streamed deltas remain the final fallback when no result field is
 * present. */
function extractPlainTextResult(result: any): string {
  const direct = [result?.response, result?.output, result?.message, result?.text, result?.content];
  for (const value of direct) {
    if (typeof value === "string" && value.trim()) return value;
    if (!value || typeof value !== "object") continue;
    if (Array.isArray(value)) {
      const content = value
        .map((item: any) => typeof item === "string" ? item : typeof item?.text === "string" ? item.text : typeof item?.content === "string" ? item.content : "")
        .filter(Boolean)
        .join("");
      if (content.trim()) return content;
      continue;
    }
    if (typeof value.text === "string" && value.text.trim()) return value.text;
    if (typeof value.message === "string" && value.message.trim()) return value.message;
    if (typeof value.content === "string" && value.content.trim()) return value.content;
    if (Array.isArray(value.content)) {
      const content = value.content
        .map((item: any) => typeof item === "string" ? item : typeof item?.text === "string" ? item.text : "")
        .filter(Boolean)
        .join("");
      if (content.trim()) return content;
    }
  }
  return "";
}

/** Whether AGY should receive a separate --effort flag for this model. */
export function shouldPassAntigravityEffort(model?: string): boolean {
  if (!model) return true;
  // Stable model slugs use a trailing reasoning tier (for example
  // `gemini-3.7-flash-high` and `gemini-3.7-flash-medium`).
  return !/(?:^|[-_])(?:low|medium|med|high)$/i.test(model.trim());
}

function looksLikeStructuredOutput(value: string): boolean {
  const trimmed = value.trimStart();
  return trimmed.startsWith("{") || trimmed.startsWith("[") || trimmed.startsWith("```") || /\"(?:schemaVersion|action|message)\"\s*:/.test(value);
}

/** Init envelopes differ slightly across AGY CLI builds. Keep diagnostics
 * intentionally coarse: only report how many tool declarations were exposed,
 * never the schemas or arguments themselves. */
function countAdvertisedTools(event: any, init: any): number {
  const candidates = [
    event?.tools,
    init?.tools,
    event?.mcp?.tools,
    init?.mcp?.tools,
    event?.mcp_tools,
    init?.mcp_tools,
  ];
  for (const value of candidates) if (Array.isArray(value)) return value.length;
  return 0;
}

function extractAdvertisedToolNames(event: any, init: any): string {
  const candidates = [
    event?.tools,
    init?.tools,
    event?.mcp?.tools,
    init?.mcp?.tools,
    event?.mcp_tools,
    init?.mcp_tools,
  ];
  for (const value of candidates) {
    if (!Array.isArray(value)) continue;
    return value
      .map((tool: any) => typeof tool === "string" ? tool : typeof tool?.name === "string" ? tool.name : "")
      .filter(Boolean)
      .slice(0, 128)
      .join(",");
  }
  return "";
}

export function extractManagerMessage(value: string): { found: boolean; complete: boolean; text: string } {
  const marker = /\"message\"\s*:\s*\"/.exec(value);
  if (!marker || marker.index < 0) return { found: false, complete: false, text: "" };
  const start = marker.index + marker[0].length;
  let text = "";
  let escaped = false;
  for (let index = start; index < value.length; index += 1) {
    const character = value[index];
    if (escaped) {
      escaped = false;
      if (character === "n") text += "\n";
      else if (character === "r") text += "\r";
      else if (character === "t") text += "\t";
      else if (character === "b") text += "\b";
      else if (character === "f") text += "\f";
      else if (character === "u") {
        const hex = value.slice(index + 1, index + 5);
        if (!/^[0-9a-f]{4}$/i.test(hex)) break;
        text += String.fromCharCode(Number.parseInt(hex, 16)); index += 4;
      } else text += character;
      continue;
    }
    if (character === "\\") { escaped = true; continue; }
    if (character === "\"") return { found: true, complete: true, text };
    text += character;
  }
  return { found: true, complete: false, text };
}

/**
 * Recovers a terminal ManagerAction from a cleanly closed AGY text stream.
 * AGY's documented protocol ends every turn with a `result` event, but a few
 * builds close after the final `agent_response` delta. The action and its
 * complete JSON string value are enough to preserve that reply safely; an
 * incomplete message is never accepted as a completed turn.
 */
export function recoverManagerActionFromStream(value: string): ManagerAction | undefined {
  const actionMatch = /\"action\"\s*:\s*\"([^\"]+)\"/.exec(value);
  const message = extractManagerMessage(value);
  if (!actionMatch || !message.found || !message.complete) return undefined;
  const schemaVersionMatch = /\"schemaVersion\"\s*:\s*(\d+)/.exec(value);
  return validateManagerAction({
    schemaVersion: schemaVersionMatch ? Number(schemaVersionMatch[1]) : 1,
    action: actionMatch[1],
    message: message.text,
  });
}

export function isAuthenticationError(text: string): boolean {
  return /authentication required|not logged in|sign in|login required|unauthenticated/i.test(text);
}

export function validateAntigravityInit(init: any, runtimeRoot: string, expectedAgent = "ilmatto-manager"): string | undefined {
  if (init?.agent !== expectedAgent) {
    return `Antigravity did not activate the required ${expectedAgent} agent (reported: ${String(init?.agent ?? "none")})`;
  }
  const actualCwd = typeof init?.cwd === "string" ? path.resolve(init.cwd) : "";
  const expectedCwd = path.resolve(runtimeRoot);
  if (!actualCwd || actualCwd.toLowerCase() !== expectedCwd.toLowerCase()) {
    return `Antigravity manager started outside its isolated runtime directory (reported: ${actualCwd || "none"})`;
  }
  if (init?.permission_mode === "always-proceed") {
    return "Antigravity manager unexpectedly started with always-proceed permissions";
  }
  // Per the Headless protocol, init.tools is the CLI-wide tool catalogue, not
  // the selected custom agent's allow-list. The actual allow-list is enforced
  // by the agent frontmatter and every executed step is checked below.
  return undefined;
}

type AntigravityToolInvocation = {
  toolName: string;
  callId: string;
  displayText: string;
  state: "started" | "completed";
};

/**
 * Converts AGY's tool step metadata into a safe, user-facing progress label.
 * Parameters are deliberately ignored: URLs, search terms, local paths and
 * other provider payloads must not leak into the transient chat bubble.
 */
function extractAntigravityToolInvocation(step: any, seenToolCalls: Set<string>): AntigravityToolInvocation | undefined {
  const rawToolName = step?.tool_name ?? step?.tool_info?.name ?? step?.tool_info?.tool_name ?? step?.tool_call?.name ??
    (step?.subagent_info ? "browser" : undefined);
  if (typeof rawToolName !== "string" || !rawToolName.trim()) return undefined;
  const toolName = rawToolName.trim();
  const normalizedTool = toolName.toLowerCase();
  const rawCallId = step?.call_id ?? step?.tool_call_id ?? step?.toolCallId ?? step?.tool_call?.call_id ??
    step?.tool_info?.call_id ?? step?.tool_info?.callId ?? step?.step_id ??
    step?.step_index ?? step?.index;
  const callId = typeof rawCallId === "string" || typeof rawCallId === "number"
    ? String(rawCallId)
    : `${normalizedTool}:${seenToolCalls.size + 1}`;

  const state = isCompletedToolStep(step) ? "completed" : "started";
  let displayText = state === "completed" ? `${toolName} 已完成` : `正在调用 ${toolName}…`;
  if (state === "started") {
    if (["search_web", "searchweb"].includes(normalizedTool)) displayText = "正在搜索网页…";
    else if (["read_url_content", "readurlcontent", "read_url"].includes(normalizedTool)) displayText = "正在读取网页内容…";
    else if (normalizedTool === "invoke_subagent" || normalizedTool === "invokesubagent" || normalizedTool === "browser" || normalizedTool.startsWith("browser_")) displayText = "正在使用浏览器…";
    else if (["view_file", "read_file", "readfile"].includes(normalizedTool)) displayText = "正在读取图片…";
  }

  return { toolName, callId, displayText, state };
}

function isCompletedToolStep(step: any): boolean {
  const type = String(step?.step_type ?? step?.type ?? "").toLowerCase();
  const status = String(step?.status ?? step?.tool_status ?? step?.tool_info?.status ?? "").toLowerCase();
  return type.includes("tool_result") || type.includes("tool_output") || type.includes("tool_complete") || type.includes("tool_end") ||
    status === "completed" || status === "complete" || status === "success" || status === "failed" || status === "error";
}

export function coordinatorStepPolicyViolation(step: any, allowedReadPaths: readonly string[] = []): string | undefined {
  if (step?.subagent_info) {
    if (isBrowserSubagent(step)) return undefined;
    return "Antigravity manager attempted to invoke a non-browser subagent";
  }
  if (step?.step_type === "tool") {
    const tool = String(step?.tool_name ?? step?.tool_info?.name ?? "unknown");
    const normalizedTool = tool.toLowerCase();
    if (["search_web", "searchweb", "read_url_content", "readurlcontent", "read_url"].includes(normalizedTool)) return undefined;
    if (normalizedTool === "invoke_subagent" || normalizedTool === "invokesubagent") {
      if (isBrowserSubagent(step)) return undefined;
      return "Antigravity manager may invoke only the built-in browser subagent";
    }
    // Some AGY builds expose the browser subagent's concrete operations as
    // browser_* tool steps instead of reporting only invoke_subagent.
    if (normalizedTool === "browser" || normalizedTool.startsWith("browser_")) return undefined;
    if (!["view_file", "read_file", "readfile"].includes(normalizedTool)) return `Antigravity manager attempted to execute forbidden tool: ${tool}`;
    const requestedPath = extractViewFilePath(step);
    if (!requestedPath) return "Antigravity manager requested view_file without a verifiable image path";
    const allowed = allowedReadPaths.some((candidate) => comparablePath(candidate) === comparablePath(requestedPath));
    if (!allowed) return "Antigravity manager requested view_file outside the current managed image set";
  }
  return undefined;
}

/** Returns the coordinator policy result for image-turn telemetry. This never blocks execution. */
export function imageLookupSoftPolicyViolation(step: any, allowedReadPaths: readonly string[] = []): string | undefined {
  return coordinatorStepPolicyViolation(step, allowedReadPaths);
}

function isBrowserSubagent(step: any): boolean {
  const parameters = step?.tool_info?.parameters ?? step?.tool_info?.arguments ?? step?.parameters ?? step?.arguments ?? {};
  const candidates = [
    parameters?.subagent,
    parameters?.subagent_type,
    parameters?.subagentType,
    parameters?.agent,
    parameters?.agent_name,
    parameters?.agentName,
    parameters?.name,
    parameters?.role,
    parameters?.type,
    step?.subagent_info?.type_name,
    step?.subagent_info?.typeName,
    step?.subagent_info?.role,
    step?.subagent_info?.name,
    ...(Array.isArray(step?.subagent_info?.subagents)
      ? step.subagent_info.subagents.flatMap((item: any) => [item?.type_name, item?.typeName, item?.role, item?.name])
      : []),
  ];
  return candidates.some((candidate) => typeof candidate === "string" && candidate.trim().toLowerCase() === "browser");
}

function extractViewFilePath(step: any): string | undefined {
  const parameters = step?.tool_info?.parameters ?? step?.tool_info?.arguments ?? step?.parameters ?? step?.arguments ?? {};
  const value = parameters?.AbsolutePath ?? parameters?.absolutePath ?? parameters?.Path ?? parameters?.path ?? parameters?.FilePath ?? parameters?.filePath ?? parameters?.target;
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function comparablePath(value: string): string {
  let normalized = value.trim();
  if ((normalized.startsWith("\"") && normalized.endsWith("\"")) || (normalized.startsWith("'") && normalized.endsWith("'"))) {
    normalized = normalized.slice(1, -1).trim();
  }
  if (/^file:\/\//i.test(normalized)) {
    try {
      const url = new URL(normalized);
      normalized = decodeURIComponent(url.pathname);
      if (/^\/[A-Za-z]:[\\/]/.test(normalized)) normalized = normalized.slice(1);
    } catch { /* Keep the original value; path.resolve below will reject a mismatch. */ }
  }
  return path.resolve(normalized).replace(/[\\/]+$/, "").toLowerCase();
}

/** AGY prints this diagnostic when --agent cannot be resolved.  The init
 * event still echoes the requested name, so checking it there alone is not
 * sufficient to detect a silent fallback to the default (tool-enabled)
 * agent. */
export function isManagerAgentFallback(text: string, agentName: string): boolean {
  const escaped = agentName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`Agent\\s+["']${escaped}["']\\s+not found,\\s+falling back`, "i").test(text);
}

function describeProcessError(error: unknown): string {
  const item = error as any;
  return [item?.message, item?.stdout, item?.stderr].filter(Boolean).join("\n").trim() || "Unknown Antigravity error";
}

function numberOrUndefined(value: unknown): number | undefined {
  const number = Number(value);
  return Number.isFinite(number) ? number : undefined;
}

export function parseAntigravityResult(result: any): AntigravityTurn {
  const candidates = [result?.structured_output, result?.structuredOutput, result?.response]
    .map(parseJsonPayload)
    .filter((value): value is Record<string, unknown> => Boolean(value));
  for (const candidate of candidates) {
    // Some AGY/Gemini builds accept the supplied JSON schema but omit a
    // `const`/version property from the serialized response.  The action and
    // message fields are still validated strictly; treat the omitted version
    // as protocol version 1 so a valid routing decision is not discarded
    // before it can reach the Coding Worker.
    const action = validateManagerAction(normalizeManagerActionPayload(candidate));
    if (action) return { action, conversationId: typeof result?.conversation_id === "string" ? result.conversation_id : undefined, cacheReadTokens: numberOrUndefined(result?.usage?.cache_read_tokens) };
  }
  throw new Error("Invalid manager action schema");
}

function normalizeManagerActionPayload(value: Record<string, unknown>): Record<string, unknown> {
  if (value.schemaVersion !== undefined) return value;
  if (typeof value.action === "string" && typeof value.message === "string") return { ...value, schemaVersion: 1 };
  return value;
}

function parseJsonPayload(value: unknown): unknown {
  if (value && typeof value === "object") return value;
  if (typeof value !== "string") return undefined;
  let text = value.trim().replace(/^```(?:json)?\s*/i, "").replace(/\s*```$/, "").trim();
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const parsed = JSON.parse(text);
      if (parsed && typeof parsed === "object") return parsed;
      if (typeof parsed === "string") { text = parsed.trim(); continue; }
      return undefined;
    } catch { break; }
  }
  const start = text.indexOf("{");
  const end = text.lastIndexOf("}");
  if (start >= 0 && end > start) {
    try { return JSON.parse(text.slice(start, end + 1)); } catch { }
  }
  return undefined;
}

/**
 * AGY's stream-json output does not currently expose its private reasoning
 * deltas. Some versions do include short, user-safe progress metadata in the
 * structured response; surface only those summaries instead of the hidden
 * chain-of-thought or the full protocol JSON.
 */
export function extractAntigravityProgress(value: string): string {
  const parsed = parseJsonPayload(value);
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return "";
  const record = parsed as Record<string, unknown>;
  const nested = record.planner_response && typeof record.planner_response === "object" && !Array.isArray(record.planner_response)
    ? record.planner_response as Record<string, unknown>
    : undefined;
  const candidates = [
    record.thinkingSummary, record.thinking_summary,
    record.reasoningSummary, record.reasoning_summary,
    record.toolAction, record.tool_action,
    record.toolSummary, record.tool_summary,
    record.progressSummary, record.progress_summary,
    nested?.thinkingSummary, nested?.thinking_summary,
    nested?.reasoningSummary, nested?.reasoning_summary,
    nested?.toolAction, nested?.tool_action,
    nested?.toolSummary, nested?.tool_summary,
  ];
  const parts = [...new Set(candidates
    .filter((candidate): candidate is string => typeof candidate === "string")
    .map((candidate) => candidate.trim())
    .filter(Boolean))];
  return parts.join(" · ").slice(0, 400);
}
