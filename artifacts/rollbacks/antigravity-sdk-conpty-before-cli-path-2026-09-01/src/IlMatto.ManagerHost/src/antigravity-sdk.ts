import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import path from "node:path";
import { existsSync } from "node:fs";
import { rm } from "node:fs/promises";
import type { ManagerRuntime } from "./runtime.js";
import type { CompanionHistoryItem, ManagerAction, ManagerImageAttachment } from "./protocol.js";
import { validateManagerAction } from "./protocol.js";
import type { CoordinatorStreamEvent } from "./antigravity.js";

export type AntigravitySdkProbe = {
  available: boolean;
  authenticated: boolean;
  version?: string;
  sessionRef?: string;
  message?: string;
};

export type AntigravitySdkConfig = {
  sessionId: string;
  runtime: ManagerRuntime;
  systemPrompt: string;
  model?: string;
  effort?: "low" | "medium" | "high";
  timeoutSeconds: number;
  sdkSessionRef?: string;
  history?: CompanionHistoryItem[];
};

export type AntigravitySdkTurn = {
  action: ManagerAction;
  sessionRef?: string;
  contextTokens?: number;
  cacheReadTokens?: number;
  totalTokens?: number;
};

type BridgeMessage = {
  type: string;
  requestId?: string;
  sessionRef?: string;
  sdkVersion?: string;
  text?: string;
  code?: string;
  message?: string;
  action?: unknown;
  usage?: { contextTokens?: number; cacheReadTokens?: number; totalTokens?: number };
};

export class AntigravitySdkSession {
  private child?: ChildProcessWithoutNullStreams;
  private stdoutBuffer = "";
  private stderr = "";
  private sessionRef?: string;
  private sdkVersion?: string;
  private pendingStart?: Pending<AntigravitySdkProbe>;
  private pendingAsk?: Pending<AntigravitySdkTurn>;
  private streamHandler?: (event: CoordinatorStreamEvent) => void;

  constructor(private readonly config: AntigravitySdkConfig) {}

  get activeSessionRef(): string | undefined { return this.sessionRef; }
  get version(): string | undefined { return this.sdkVersion; }

  async start(): Promise<AntigravitySdkProbe> {
    if (this.child && !this.child.killed && this.sessionRef) {
      return { available: true, authenticated: true, version: this.sdkVersion, sessionRef: this.sessionRef };
    }
    this.ensureStarted();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingStart = undefined;
        this.stopProcess();
        reject(new SdkBridgeError("SDK_START_TIMEOUT", `Antigravity SDK Bridge 启动超时（${this.config.timeoutSeconds} 秒）。`));
      }, this.config.timeoutSeconds * 1000);
      this.pendingStart = { resolve, reject, timer };
      this.write({
        type: "start",
        sessionId: this.config.sessionId,
        conversationId: this.config.sdkSessionRef,
        systemPrompt: this.config.systemPrompt,
        model: this.config.model,
        effort: this.config.effort,
        saveDir: sdkSessionRoot(),
        history: this.config.history ?? [],
      });
    });
  }

  async ask(text: string, attachments: ManagerImageAttachment[] = [], onStream?: (event: CoordinatorStreamEvent) => void): Promise<AntigravitySdkTurn> {
    if (!this.child || this.child.killed || !this.sessionRef) await this.start();
    if (this.pendingAsk) throw new SdkBridgeError("SDK_BUSY", "Antigravity SDK 正在处理上一条消息。");
    this.streamHandler = onStream;
    const requestId = crypto.randomUUID();
    try {
      return await new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          this.pendingAsk = undefined;
          this.stopProcess();
          reject(new SdkBridgeError("SDK_TIMEOUT", `Antigravity SDK 请求已超过 ${this.config.timeoutSeconds} 秒。`));
        }, this.config.timeoutSeconds * 1000);
        this.pendingAsk = { resolve, reject, timer, requestId };
        this.write({ type: "ask", requestId, text, attachments });
      });
    } finally {
      this.streamHandler = undefined;
    }
  }

  cancel(): void {
    if (this.pendingAsk) {
      clearTimeout(this.pendingAsk.timer);
      this.pendingAsk.reject(new SdkBridgeError("SDK_CANCELLED", "Antigravity SDK 请求已取消。"));
      this.pendingAsk = undefined;
    }
    if (this.child?.stdin.writable) {
      try { this.child.stdin.write(`${JSON.stringify({ type: "cancel" })}\n`, "utf8"); } catch { }
    }
    this.stopProcess();
  }

  async dispose(): Promise<void> {
    const child = this.child;
    if (child?.stdin.writable) {
      try { child.stdin.write(`${JSON.stringify({ type: "shutdown" })}\n`, "utf8"); } catch { }
    }
    if (child && !child.killed) {
      await new Promise<void>((resolve) => {
        const timer = setTimeout(() => { this.stopProcess(); resolve(); }, 1_500);
        child.once("exit", () => { clearTimeout(timer); resolve(); });
      });
    }
    this.stopProcess();
  }

  private ensureStarted(): void {
    if (this.child && !this.child.killed) return;
    const bridge = findBridgeScript();
    if (!bridge || !existsSync(bridge)) {
      throw new SdkBridgeError("SDK_BRIDGE_NOT_FOUND", "找不到 Antigravity SDK Bridge。请检查应用目录或 ILMATTO_ANTIGRAVITY_BRIDGE 配置。");
    }
    const executable = findPythonExecutable(bridge);
    const child = spawn(executable, [bridge], {
      cwd: this.config.runtime.root,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
      env: { ...process.env, PYTHONUNBUFFERED: "1" },
    });
    this.child = child;
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => this.onStdout(chunk));
    child.stderr.on("data", (chunk: string) => { this.stderr = `${this.stderr}${chunk}`.slice(-32_000); });
    child.on("error", (error) => this.fail(error));
    child.on("exit", (code) => {
      if (this.pendingStart) this.fail(new SdkBridgeError("SDK_EXITED", this.stderr.trim() || `Antigravity SDK Bridge 退出（${code ?? "unknown"}）。`));
      if (this.pendingAsk) this.fail(new SdkBridgeError("SDK_EXITED", this.stderr.trim() || `Antigravity SDK Bridge 退出（${code ?? "unknown"}）。`));
      if (this.child === child) this.child = undefined;
    });
  }

  private write(value: Record<string, unknown>): void {
    if (!this.child?.stdin.writable) throw new SdkBridgeError("SDK_NOT_RUNNING", "Antigravity SDK Bridge 未运行。");
    this.child.stdin.write(`${JSON.stringify(value)}\n`, "utf8", (error) => { if (error) this.fail(error); });
  }

  private onStdout(chunk: string): void {
    this.stdoutBuffer += chunk;
    let newline = this.stdoutBuffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.stdoutBuffer.slice(0, newline).trim();
      this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1);
      newline = this.stdoutBuffer.indexOf("\n");
      if (!line) continue;
      let message: BridgeMessage;
      try { message = JSON.parse(line) as BridgeMessage; } catch { continue; }
      this.handleMessage(message);
    }
  }

  private handleMessage(message: BridgeMessage): void {
    if (message.type === "ready") {
      this.sessionRef = message.sessionRef ?? this.sessionRef;
      this.sdkVersion = message.sdkVersion ?? this.sdkVersion;
      const pending = this.pendingStart;
      if (!pending) return;
      clearTimeout(pending.timer);
      this.pendingStart = undefined;
      pending.resolve({ available: true, authenticated: true, version: this.sdkVersion, sessionRef: this.sessionRef });
      return;
    }
    if (message.type === "text_delta" || message.type === "status") {
      if (message.type === "text_delta" && message.text) this.emitStream("text", message.text);
      else if (message.text) this.emitStream("thinking", message.text);
      return;
    }
    if (message.type === "error") {
      const error = new SdkBridgeError(message.code ?? "SDK_REQUEST_FAILED", message.message ?? "Antigravity SDK 请求失败。");
      if (message.requestId && this.pendingAsk?.requestId !== message.requestId) return;
      this.fail(error);
      return;
    }
    if (message.type !== "completed") return;
    if (message.requestId && this.pendingAsk?.requestId !== message.requestId) return;
    const action = validateManagerAction(message.action);
    if (!action) { this.fail(new SdkBridgeError("SDK_SCHEMA_INVALID", "Antigravity SDK 返回的 ManagerAction 无效。")); return; }
    const pending = this.pendingAsk;
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pendingAsk = undefined;
    this.sessionRef = message.sessionRef ?? this.sessionRef;
    pending.resolve({
      action,
      sessionRef: this.sessionRef,
      contextTokens: message.usage?.contextTokens,
      cacheReadTokens: message.usage?.cacheReadTokens,
      totalTokens: message.usage?.totalTokens,
    });
  }

  private emitStream(kind: CoordinatorStreamEvent["kind"], text: string): void {
    try { this.streamHandler?.({ kind, text }); } catch { }
  }

  private fail(error: Error): void {
    const start = this.pendingStart;
    if (start) { clearTimeout(start.timer); this.pendingStart = undefined; start.reject(error); }
    const ask = this.pendingAsk;
    if (ask) { clearTimeout(ask.timer); this.pendingAsk = undefined; ask.reject(error); }
  }

  private stopProcess(): void {
    const child = this.child;
    this.child = undefined;
    if (!child || child.killed) return;
    try { child.stdin.end(); } catch { }
    if (process.platform === "win32" && child.pid) {
      try { spawn("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" }); }
      catch { try { child.kill(); } catch { } }
    } else try { child.kill(); } catch { }
  }
}

export class SdkBridgeError extends Error {
  constructor(readonly code: string, message: string) { super(message); }
}

type Pending<T> = { resolve: (value: T) => void; reject: (error: Error) => void; timer: NodeJS.Timeout; requestId?: string };

export function findBridgeScript(): string {
  const candidates = [
    process.env.ILMATTO_ANTIGRAVITY_BRIDGE,
    path.resolve(process.cwd(), "..", "AntigravityBridge", "bridge.py"),
    path.resolve(process.cwd(), "..", "..", "..", "IlMatto.AntigravityBridge", "bridge.py"),
    path.resolve(process.cwd(), "..", "..", "..", "..", "IlMatto.AntigravityBridge", "bridge.py"),
  ].filter((value): value is string => Boolean(value));
  // Path probing is kept synchronous at startup so Coordinator construction
  // remains deterministic and does not create a second async initialization
  // protocol just to locate a static bundled file.
  const found = candidates.find(candidate => existsSync(candidate));
  return found ?? candidates[0] ?? "";
}

export function findPythonExecutable(bridgeScript = findBridgeScript()): string {
  const bridgeDirectory = path.dirname(bridgeScript);
  const development = path.join(
    bridgeDirectory,
    ".venv",
    "Scripts",
    process.platform === "win32" ? "python.exe" : "python",
  );
  const packaged = path.join(path.dirname(bridgeScript), "python", process.platform === "win32" ? "python.exe" : "bin/python");
  return process.env.ILMATTO_ANTIGRAVITY_PYTHON?.trim()
    || (existsSync(packaged) ? packaged : undefined)
    || (existsSync(development) ? development : "python");
}

export function sdkSessionRoot(): string {
  const localAppData = process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE || process.cwd(), "AppData", "Local");
  return path.resolve(process.env.ILMATTO_ANTIGRAVITY_SDK_SESSION_DIR || path.join(localAppData, "IlMatto", "manager-sessions", "antigravity-sdk"));
}

export async function deleteAntigravitySdkSession(sessionRef: string | undefined): Promise<void> {
  if (!sessionRef || !/^[A-Za-z0-9-]{32,}$/.test(sessionRef)) return;
  const root = path.resolve(sdkSessionRoot());
  const target = path.resolve(root, sessionRef);
  if (path.dirname(target).toLowerCase() !== root.toLowerCase()) throw new SdkBridgeError("SDK_SESSION_DELETE_INVALID", "SDK 会话目录无效。");
  await rm(target, { recursive: true, force: true });
}
