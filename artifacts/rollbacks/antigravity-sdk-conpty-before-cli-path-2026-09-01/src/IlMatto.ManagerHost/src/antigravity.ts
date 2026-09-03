import { spawn, execFile, type ExecFileOptions } from "node:child_process";
import path from "node:path";
import type { ChildProcessWithoutNullStreams } from "node:child_process";
import type { ManagerAction } from "./protocol.js";
import { normalizeAntigravityModelId, validateManagerAction } from "./protocol.js";
import type { ManagerRuntime } from "./runtime.js";

export type AntigravityProbe = { available: boolean; authenticated: boolean; authenticationRequired?: boolean; version?: string; message?: string };
export type AntigravityTurn = { action: ManagerAction; conversationId?: string; cacheReadTokens?: number };
export type CoordinatorStreamEvent = { kind: "text" | "thinking"; text: string };

export async function probeAntigravity(executable: string, cwd?: string): Promise<AntigravityProbe> {
  try {
    const versionResult = await execFileWithClosedStdin(executable, ["--version"], { timeout: 10_000, windowsHide: true, cwd });
    const version = `${versionResult.stdout}${versionResult.stderr}`.trim();
    try {
      await execFileWithClosedStdin(executable, ["models"], { timeout: 20_000, windowsHide: true, maxBuffer: 1_000_000, cwd });
      return { available: true, authenticated: true, authenticationRequired: false, version };
    } catch (error) {
      const text = describeProcessError(error);
      return { available: true, authenticated: false, authenticationRequired: isAuthenticationError(text), version, message: text };
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

export class AntigravitySession {
  private child?: ChildProcessWithoutNullStreams;
  private stdoutBuffer = "";
  private stderr = "";
  private agentFallbackDetected = false;
  private conversationId?: string;
  private pending?: { resolve: (value: AntigravityTurn) => void; reject: (error: Error) => void; timer: NodeJS.Timeout };
  private policyVerified = false;
  private streamHandler?: (event: CoordinatorStreamEvent) => void;
  private streamedResponse = "";
  private streamedMessage = "";
  private streamedProgressSummary = "";

  constructor(
    private readonly executable: string,
    private readonly runtime: ManagerRuntime,
    private readonly timeoutSeconds: number,
    private readonly effort: "low" | "medium" | "high",
    private readonly model?: string,
    resumeConversationId?: string,
  ) { this.conversationId = resumeConversationId; }

  get activeConversationId(): string | undefined { return this.conversationId; }

  async ask(userMessage: string, onStream?: (event: CoordinatorStreamEvent) => void): Promise<AntigravityTurn> {
    if (this.pending) throw new Error("Antigravity is already processing a turn");
    this.streamHandler = onStream;
    this.ensureStarted();
    try {
      return await this.sendPrompt(userMessage);
    } catch (error) {
      if (!/invalid manager action schema/i.test(error instanceof Error ? error.message : "")) throw error;
      this.ensureStarted();
      return this.sendPrompt("Your previous response violated the required JSON schema. Return only a valid manager action object. Do not add technical content.");
    } finally {
      this.streamHandler = undefined;
    }
  }

  cancel(): void {
    const error = new Error("Antigravity turn cancelled");
    if (this.pending) {
      clearTimeout(this.pending.timer);
      this.pending.reject(error);
      this.pending = undefined;
    }
    this.stopProcess();
  }

  dispose(): void { this.cancel(); }

  private ensureStarted(): void {
    if (this.child && !this.child.killed) return;
    const model = normalizeAntigravityModelId(this.model);
    const args = [
      "--agent", this.runtime.agentName,
      "--input-format", "stream-json",
      "--output-format", "stream-json",
      "--json-schema", this.runtime.schemaPath,
      "--sandbox",
      "--print-timeout", `${Math.max(10, this.timeoutSeconds)}s`,
      "--log-file", this.runtime.logPath,
    ];
    // `agy models` exposes reasoning variants as concrete slugs such as
    // `gemini-3.7-flash-high`.  Passing --effort again for one of these
    // pinned variants is rejected by AGY ("--effort is not supported").
    // Keep --effort for base/custom model ids where the flag is meaningful.
    if (shouldPassAntigravityEffort(model)) args.push("--effort", this.effort);
    if (model) args.push("--model", model);
    if (this.conversationId) args.push("--conversation", this.conversationId);
    this.stderr = "";
    this.agentFallbackDetected = false;
    this.policyVerified = false;
    this.stdoutBuffer = "";
    const child = spawn(this.executable, args, { cwd: this.runtime.root, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    this.child = child;
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => this.onStdout(chunk));
    child.stderr.on("data", (chunk: string) => {
      this.stderr = `${this.stderr}${chunk}`.slice(-32_000);
      if (isManagerAgentFallback(this.stderr, this.runtime.agentName)) this.agentFallbackDetected = true;
    });
    child.on("error", (error) => this.failPending(error));
    child.on("exit", (code) => {
      if (this.pending) this.failPending(new Error(this.stderr.trim() || `Antigravity exited with code ${code ?? "unknown"}`));
      if (this.child === child) this.child = undefined;
    });
  }

  private sendPrompt(content: string): Promise<AntigravityTurn> {
    this.streamedResponse = "";
    this.streamedMessage = "";
    this.streamedProgressSummary = "";
    return new Promise((resolve, reject) => {
      if (!this.child?.stdin.writable) { reject(new Error("Antigravity stdin is unavailable")); return; }
      const timer = setTimeout(() => {
        this.pending = undefined;
        this.stopProcess();
        reject(new Error(`Antigravity timed out after ${this.timeoutSeconds} seconds`));
      }, this.timeoutSeconds * 1000);
      this.pending = { resolve, reject, timer };
      this.child.stdin.write(`${JSON.stringify({ event: "user", message: { content } })}\n`, "utf8", (error) => {
        if (error) this.failPending(error);
      });
    });
  }

  private onStdout(chunk: string): void {
    this.stdoutBuffer += chunk;
    let newline = this.stdoutBuffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.stdoutBuffer.slice(0, newline).trim();
      this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1);
      newline = this.stdoutBuffer.indexOf("\n");
      if (!line) continue;
      let event: any;
      try { event = JSON.parse(line); } catch { continue; }
      if (event.event === "init") {
        this.conversationId = event.conversation_id ?? event.init?.conversation_id ?? this.conversationId;
        const policyError = validateAntigravityInit(event.init, this.runtime.root, this.runtime.agentName);
        if (policyError) {
          this.failPending(new Error(policyError));
          this.stopProcess();
          return;
        }
        this.policyVerified = true;
      }
      if (event.event === "step_update") {
        this.handleStreamStep(event.step_update);
        if (this.agentFallbackDetected) {
          this.failPending(new Error(`Antigravity did not load the isolated manager agent ${this.runtime.agentName}; refusing to continue with the default tool-enabled agent`));
          this.stopProcess();
          return;
        }
        const policyError = coordinatorStepPolicyViolation(event.step_update);
        if (policyError) {
          this.failPending(new Error(policyError));
          this.stopProcess();
          return;
        }
      }
      if (event.event !== "result") continue;
      const result = event.result ?? {};
      if (this.agentFallbackDetected) {
        this.failPending(new Error(`Antigravity did not load the isolated manager agent ${this.runtime.agentName}; refusing to continue with the default tool-enabled agent`));
        this.stopProcess();
        return;
      }
      this.conversationId = result.conversation_id ?? this.conversationId;
      if (result.status !== "SUCCESS") {
        this.failPending(new Error(String(result.error ?? (this.stderr.trim() || "Antigravity request failed"))));
        continue;
      }
      if (!this.policyVerified) {
        this.failPending(new Error(`Antigravity did not confirm the isolated ${this.runtime.agentName} runtime`));
        continue;
      }
      let parsed: AntigravityTurn;
      try { parsed = parseAntigravityResult(result); }
      catch {
        this.failPending(new Error("Antigravity returned an invalid manager action schema"));
        continue;
      }
      const pending = this.pending;
      if (!pending) continue;
      clearTimeout(pending.timer);
      this.pending = undefined;
      pending.resolve({ ...parsed, conversationId: this.conversationId });
    }
  }

  private failPending(error: Error): void {
    const pending = this.pending;
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pending = undefined;
    pending.reject(error);
  }

  private stopProcess(): void {
    const child = this.child;
    this.child = undefined;
    if (!child || child.killed) return;
    try { child.stdin.end(); } catch { }
    if (process.platform === "win32" && child.pid) {
      try { spawn("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" }); } catch { try { child.kill(); } catch { } }
    } else try { child.kill(); } catch { }
  }

  private handleStreamStep(step: any): void {
    if (!step || !this.streamHandler) return;
    const stepProgress = extractAntigravityProgress(JSON.stringify(step));
    if (stepProgress) this.appendProgressSummary(stepProgress);

    const type = String(step.step_type ?? "").toLowerCase();
    const explicitThinking = [step.thinking_delta, step.reasoning_delta, step.thought_delta]
      .find((value) => typeof value === "string" && value.length > 0);
    if (typeof explicitThinking === "string") this.emitStream("thinking", explicitThinking);

    const delta = typeof step.text_delta === "string" ? step.text_delta : "";
    if (!delta) return;
    // AGY currently emits only text_delta for agent_response steps. Keep a
    // useful inline process row even when the CLI withholds reasoning deltas.
    if (!explicitThinking && !stepProgress && !this.streamedResponse && (type.includes("agent_response") || type === "response" || type === "")) {
      this.emitStream("thinking", "正在分析并生成路由…");
    }
    if (type.includes("think") || type.includes("reason")) {
      this.emitStream("thinking", delta);
      return;
    }
    if (type === "agent_response" || type === "response" || type === "") this.handleResponseDelta(delta);
  }

  private handleResponseDelta(delta: string): void {
    this.streamedResponse += delta;
    const progress = extractAntigravityProgress(this.streamedResponse);
    if (progress) this.appendProgressSummary(progress);
    const extracted = extractManagerMessage(this.streamedResponse);
    if (extracted.found) {
      if (extracted.text.startsWith(this.streamedMessage)) {
        const suffix = extracted.text.slice(this.streamedMessage.length);
        this.streamedMessage = extracted.text;
        if (suffix) this.emitStream("text", suffix);
      }
      return;
    }
    // With --json-schema AGY streams the serialized object. Do not expose
    // that protocol JSON in the chat; plain-text responses from older builds
    // are still forwarded as they arrive.
    if (!looksLikeStructuredOutput(this.streamedResponse)) this.emitStream("text", delta);
  }

  private appendProgressSummary(summary: string): void {
    const suffix = summary.startsWith(this.streamedProgressSummary)
      ? summary.slice(this.streamedProgressSummary.length)
      : summary;
    this.streamedProgressSummary = summary;
    if (suffix) this.emitStream("thinking", suffix);
  }

  private emitStream(kind: CoordinatorStreamEvent["kind"], text: string): void {
    if (!text) return;
    try { this.streamHandler?.({ kind, text }); } catch { /* UI callbacks must not break the AGY session. */ }
  }
}

/** Whether AGY should receive a separate --effort flag for this model. */
export function shouldPassAntigravityEffort(model?: string): boolean {
  if (!model) return true;
  // Stable model slugs use a trailing reasoning tier (for example
  // `gemini-3.7-flash-high` and `gemini-3.7-flash-medium`).
  return !/(?:^|[-_])(?:low|medium|high)$/i.test(model.trim());
}

function looksLikeStructuredOutput(value: string): boolean {
  const trimmed = value.trimStart();
  return trimmed.startsWith("{") || trimmed.startsWith("[") || trimmed.startsWith("```") || /\"(?:schemaVersion|action|message)\"\s*:/.test(value);
}

export function extractManagerMessage(value: string): { found: boolean; text: string } {
  const marker = /\"message\"\s*:\s*\"/.exec(value);
  if (!marker || marker.index < 0) return { found: false, text: "" };
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
    if (character === "\"") return { found: true, text };
    text += character;
  }
  return { found: true, text };
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

export function coordinatorStepPolicyViolation(step: any): string | undefined {
  if (step?.subagent_info) return "Antigravity manager attempted to invoke a subagent";
  if (step?.step_type === "tool") {
    const tool = String(step?.tool_name ?? step?.tool_info?.name ?? "unknown");
    return `Antigravity manager attempted to execute forbidden tool: ${tool}`;
  }
  return undefined;
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
