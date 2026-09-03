import { randomUUID } from "node:crypto";
import type { ManagerRuntime } from "./runtime.js";
import { normalizeAntigravityModelId, validateManagerAction, type ManagerAction, type ManagerClientMessage, type ManagerHostMessage, type ManagerImageAttachment } from "./protocol.js";
import type { AntigravityTurn, CoordinatorStreamEvent } from "./antigravity.js";

type InteractiveResponse = Extract<ManagerClientMessage, { type: "interactive_cli_response" }>;
type Send = (message: ManagerHostMessage) => void;

type PendingRequest = {
  resolve: (response: InteractiveResponse) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
};

function interactiveResponseError(response: InteractiveResponse): InteractiveCliError {
  const detail = response.message || response.text || response.code || "Interactive Antigravity CLI failed";
  return new InteractiveCliError(response.code ?? "AGY_INTERACTIVE_FAILED", detail);
}

/**
 * Small RPC adapter for the reverse half of the existing Manager Named Pipe.
 * The desktop process owns Windows APIs; ManagerHost owns the semantic CLI
 * session and never touches the system clipboard directly.
 */
export class InteractiveCliGateway {
  private readonly pending = new Map<string, PendingRequest>();
  private outputHandler?: (text: string) => void;

  constructor(private readonly sessionId: string, private readonly send: Send, private readonly timeoutMs: number) {}

  onOutput(handler: (text: string) => void): void { this.outputHandler = handler; }

  request(operation: Extract<ManagerHostMessage, { type: "interactive_cli_request" }>["operation"], options: Omit<Extract<ManagerHostMessage, { type: "interactive_cli_request" }>, "type" | "sessionId" | "requestId" | "operation"> = {}): Promise<InteractiveResponse> {
    const requestId = randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`Interactive Antigravity request timed out: ${operation}`));
      }, this.timeoutMs);
      this.pending.set(requestId, { resolve, reject, timer });
      this.send({ type: "interactive_cli_request", sessionId: this.sessionId, requestId, operation, ...options });
    });
  }

  handle(response: InteractiveResponse): void {
    if (response.sessionId !== this.sessionId) return;
    if (response.event === "output") {
      if (response.text) this.outputHandler?.(response.text);
      return;
    }
    if (response.event === "exited" || response.event === "error") {
      const pending = this.pending.get(response.requestId);
      if (pending) {
        this.pending.delete(response.requestId);
        clearTimeout(pending.timer);
        pending.reject(interactiveResponseError(response));
      } else {
        this.fail(interactiveResponseError(response));
      }
      return;
    }
    const pending = this.pending.get(response.requestId);
    if (!pending) return;
    this.pending.delete(response.requestId);
    clearTimeout(pending.timer);
    pending.resolve(response);
  }

  fail(error: Error): void {
    for (const [requestId, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
      this.pending.delete(requestId);
    }
  }
}

export type InteractiveAntigravityOptions = {
  executable: string;
  runtime: ManagerRuntime;
  timeoutSeconds: number;
  effort: "low" | "medium" | "high";
  model?: string;
  conversationId?: string;
  sessionId: string;
  send: Send;
};

/**
 * Antigravity's interactive TUI adapter. It only supports one image per
 * request in v1. The image path is an IlMatto-managed file and is sent to the
 * desktop clipboard broker; it is never inserted into the prompt text.
 */
export class AntigravityInteractiveSession {
  private readonly gateway: InteractiveCliGateway;
  private readonly timeoutMs: number;
  private conversationId?: string;
  private started = false;
  private pendingTurn?: { resolve: (turn: AntigravityTurn) => void; reject: (error: Error) => void; timer: NodeJS.Timeout };
  private streamHandler?: (event: CoordinatorStreamEvent) => void;
  private output = "";
  private emittedMessage = "";

  constructor(private readonly options: InteractiveAntigravityOptions) {
    this.conversationId = options.conversationId;
    this.timeoutMs = Math.min(600, Math.max(10, options.timeoutSeconds)) * 1000;
    this.gateway = new InteractiveCliGateway(options.sessionId, options.send, this.timeoutMs);
    this.gateway.onOutput((text) => this.onOutput(text));
  }

  get activeConversationId(): string | undefined { return this.conversationId; }

  async ask(userMessage: string, onStream?: (event: CoordinatorStreamEvent) => void, attachments: ManagerImageAttachment[] = []): Promise<AntigravityTurn> {
    if (attachments.length > 1) throw new InteractiveCliError("AGY_IMAGE_LIMIT", "Antigravity CLI 第一版每条消息只支持一张图片。请移除多余图片后重试。");
    if (this.pendingTurn) throw new InteractiveCliError("BUSY", "Antigravity is already processing a turn");
    this.streamHandler = onStream;
    let clipboardLeased = false;
    try {
      await this.ensureStarted();
      this.output = "";
      this.emittedMessage = "";
      if (attachments.length === 1) {
        await this.gateway.request("paste_image", { attachmentPath: attachments[0].path });
        clipboardLeased = true;
      }
      await this.gateway.request("write_text", { text: userMessage });
      const turn = this.waitForTurn();
      try {
        await this.gateway.request("submit");
        return await turn;
      } catch (error) {
        this.rejectPendingTurn(error instanceof Error ? error : new Error(String(error)));
        throw error;
      }
    } finally {
      if (clipboardLeased) {
        try { await this.gateway.request("release_clipboard"); } catch { /* Clipboard restoration is best effort. */ }
      }
      this.streamHandler = undefined;
    }
  }

  cancel(): void {
    const pending = this.pendingTurn;
    if (pending) {
      clearTimeout(pending.timer);
      this.pendingTurn = undefined;
      pending.reject(new InteractiveCliError("CANCELLED", "Antigravity turn cancelled"));
    }
    void this.gateway.request("cancel").catch(() => undefined);
  }

  async dispose(): Promise<void> {
    this.cancel();
    try { await this.gateway.request("release_clipboard"); } catch { }
    try { await this.gateway.request("shutdown"); } catch { }
    this.gateway.fail(new Error("Interactive Antigravity session disposed"));
    this.started = false;
  }

  handleResponse(response: InteractiveResponse): void {
    this.gateway.handle(response);
    if (response.event === "exited" || response.event === "error") {
      this.started = false;
      this.rejectPendingTurn(interactiveResponseError(response));
    }
  }

  private async ensureStarted(): Promise<void> {
    if (this.started) return;
    const response = await this.gateway.request("start", {
      executable: this.options.executable,
      workingDirectory: this.options.runtime.root,
      conversationId: this.conversationId,
      model: normalizeAntigravityModelId(this.options.model),
      effort: this.options.effort,
      agentName: this.options.runtime.agentName,
      logPath: this.options.runtime.logPath,
    });
    this.conversationId = response.conversationId ?? this.conversationId;
    this.started = true;
  }

  private waitForTurn(): Promise<AntigravityTurn> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingTurn = undefined;
        reject(new InteractiveCliError("AGY_INTERACTIVE_TIMEOUT", `Antigravity interactive CLI timed out after ${this.options.timeoutSeconds} seconds`));
      }, this.timeoutMs);
      this.pendingTurn = { resolve, reject, timer };
    });
  }

  private rejectPendingTurn(error: Error): void {
    const pending = this.pendingTurn;
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pendingTurn = undefined;
    pending.reject(error);
  }

  private onOutput(chunk: string): void {
    const text = stripTerminalControlSequences(chunk);
    if (!text) return;
    this.output = `${this.output}${text}`.slice(-128_000);
    if (containsForbiddenInteractiveActivity(this.output)) {
      const error = new InteractiveCliError("AGY_INTERACTIVE_POLICY_VIOLATION", "Antigravity 交互式通道检测到工具、MCP 或子 Agent 活动，已终止本次请求。");
      this.started = false;
      this.rejectPendingTurn(error);
      this.gateway.fail(error);
      void this.gateway.request("cancel").catch(() => undefined);
      return;
    }
    const extracted = extractManagerMessageIncrementally(this.output);
    if (extracted.found && extracted.text.startsWith(this.emittedMessage)) {
      const suffix = extracted.text.slice(this.emittedMessage.length);
      this.emittedMessage = extracted.text;
      if (suffix) this.emit({ kind: "text", text: suffix });
    }
    const action = parseInteractiveAction(this.output);
    if (!action || !this.pendingTurn) return;
    const pending = this.pendingTurn;
    this.pendingTurn = undefined;
    clearTimeout(pending.timer);
    pending.resolve({ action, conversationId: this.conversationId });
  }

  private emit(event: CoordinatorStreamEvent): void {
    if (!event.text) return;
    try { this.streamHandler?.(event); } catch { }
  }
}

export class InteractiveCliError extends Error {
  constructor(readonly code: string, message: string) { super(message); }
}

export function stripTerminalControlSequences(value: string): string {
  return value
    .replace(/\u001b\][^\u0007]*(?:\u0007|\u001b\\)/g, "")
    .replace(/\u001b\[[0-?]*[ -\/]*[@-~]/g, "")
    .replace(/\u001b[()][0-2A-Z-a-z]/g, "")
    .replace(/\r/g, "");
}

export function extractManagerMessageIncrementally(value: string): { found: boolean; text: string } {
  const marker = /["']message["']\s*:\s*["']/.exec(value);
  if (!marker) return { found: false, text: "" };
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
        if (!/^[0-9a-f]{4}$/i.test(hex)) return { found: true, text };
        text += String.fromCharCode(Number.parseInt(hex, 16)); index += 4;
      } else text += character;
      continue;
    }
    if (character === "\\") { escaped = true; continue; }
    if (character === "\"" || character === "'") return { found: true, text };
    text += character;
  }
  return { found: true, text };
}

export function parseInteractiveAction(value: string): ManagerAction | undefined {
  for (const candidate of balancedJsonObjects(value)) {
    try {
      const parsed = JSON.parse(candidate);
      const action = validateManagerAction(parsed && parsed.schemaVersion === undefined ? { ...parsed, schemaVersion: 1 } : parsed);
      if (action) return action;
    } catch { }
  }
  return undefined;
}

export function containsForbiddenInteractiveActivity(value: string): boolean {
  return /"(?:tool|tool_name|tool_info|subagent|subagent_info|mcp|mcp_server)"\s*:/i.test(value) ||
    /\b(?:tool call|subagent|mcp server|run_command|write_file|read_file|browser tool)\b/i.test(value);
}

function balancedJsonObjects(value: string): string[] {
  const results: string[] = [];
  let start = -1;
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let index = 0; index < value.length; index += 1) {
    const character = value[index];
    if (inString) {
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === '"') inString = false;
      continue;
    }
    if (character === '"') { inString = true; continue; }
    if (character === "{" && depth === 0) start = index;
    if (character === "{" && start >= 0) depth += 1;
    if (character === "}" && depth > 0) {
      depth -= 1;
      if (depth === 0 && start >= 0) { results.push(value.slice(start, index + 1)); start = -1; }
    }
  }
  return results;
}
