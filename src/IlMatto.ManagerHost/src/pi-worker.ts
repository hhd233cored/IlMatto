import net from "node:net";
import path from "node:path";
import process from "node:process";
import { spawn, type ChildProcess } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type { CodingWorker } from "./coding-worker.js";
import type { ManagerImageAttachment } from "./protocol.js";

export type PiWorkerSettings = {
  sessionId: string;
  workspacePath: string;
  baseUrl: string;
  modelId: string;
  apiKey?: string;
  sessionFile?: string;
  autoApproveSafeCommands: boolean;
  autoApproveGitOperations: boolean;
};

export class PiWorkerBridge implements CodingWorker {
  readonly provider = "pi" as const;
  private child?: ChildProcess;
  private socket?: net.Socket;
  private buffer = "";
  private started = false;

  constructor(private readonly settings: PiWorkerSettings, private readonly onEvent: (event: any) => void) {}

  get sessionRef(): string | undefined { return this.settings.sessionFile; }

  async start(): Promise<void> {
    if (this.started) return;
    const pipeName = `IlMatto-Worker-${process.pid}-${crypto.randomUUID().replaceAll("-", "")}`;
    const script = findAgentHostScript();
    this.child = spawn(process.execPath, [script, "--pipe", pipeName], { cwd: path.dirname(script), windowsHide: true, stdio: ["ignore", "ignore", "pipe"] });
    let stderr = "";
    this.child.stderr?.setEncoding("utf8");
    this.child.stderr?.on("data", (chunk: string) => { stderr = `${stderr}${chunk}`.slice(-32_000); });
    this.child.on("exit", (code) => {
      this.started = false;
      if (code && code !== 0) this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "WORKER_EXITED", message: stderr || `Pi Worker exited with code ${code}` });
    });
    this.socket = await connectNamedPipe(pipeName, 8_000);
    this.socket.setEncoding("utf8");
    this.socket.on("data", (chunk: string) => this.onData(chunk));
    this.socket.on("error", (error) => this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "WORKER_PIPE_ERROR", message: error.message }));
    this.started = true;
    this.send({
      type: "start_session", sessionId: this.settings.sessionId, mode: "coding_worker",
      workspacePath: this.settings.workspacePath, baseUrl: this.settings.baseUrl, modelId: this.settings.modelId,
      apiKey: this.settings.apiKey, sessionFile: this.settings.sessionFile,
      autoApproveSafeCommands: this.settings.autoApproveSafeCommands,
      autoApproveGitOperations: this.settings.autoApproveGitOperations,
    });
  }

  sendCodeTask(taskId: string, userRequest: string, _attachments?: ManagerImageAttachment[]): void { this.send({ type: "code_task", sessionId: this.settings.sessionId, taskId, userRequest }); }
  approve(callId: string, approved: boolean): void { this.send({ type: "approve_tool_call", sessionId: this.settings.sessionId, callId, approved }); }
  resolve(callId: string, approved: boolean): void { this.approve(callId, approved); }
  cancel(): void { if (this.started) this.send({ type: "cancel", sessionId: this.settings.sessionId }); }
  deleteSession(sessionFile?: string): void { this.send({ type: "delete_session", sessionId: this.settings.sessionId, sessionFile }); }

  async dispose(): Promise<void> {
    if (this.started) {
      try { this.send({ type: "shutdown", sessionId: this.settings.sessionId }); } catch { }
    }
    this.socket?.end();
    this.socket?.destroy();
    this.started = false;
    const child = this.child;
    if (child && !child.killed) {
      await new Promise<void>((resolve) => {
        const timer = setTimeout(() => { try { child.kill(); } catch { } resolve(); }, 2_000);
        child.once("exit", () => { clearTimeout(timer); resolve(); });
      });
    }
  }

  private send(message: unknown): void {
    if (!this.socket?.writable) throw new Error("Pi Worker is not connected");
    this.socket.write(`${JSON.stringify(message)}\n`);
  }

  private onData(chunk: string): void {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      newline = this.buffer.indexOf("\n");
      if (!line) continue;
      try { this.onEvent(JSON.parse(line)); }
      catch { this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "WORKER_PROTOCOL_ERROR", message: "Pi Worker returned invalid NDJSON" }); }
    }
  }
}

function findAgentHostScript(): string {
  const directory = path.dirname(fileURLToPath(import.meta.url));
  const candidates = [
    process.env.ILMATTO_AGENT_HOST_SCRIPT,
    path.resolve(directory, "..", "..", "IlMatto.AgentHost", "dist", "index.js"),
    path.resolve(directory, "..", "..", "AgentHost", "dist", "index.js"),
    path.resolve(directory, "..", "..", "..", "IlMatto.AgentHost", "dist", "index.js"),
  ].filter((item): item is string => Boolean(item));
  const found = candidates.find(existsSync);
  if (!found) throw new Error("Cannot locate IlMatto.AgentHost/dist/index.js");
  return found;
}

async function connectNamedPipe(pipeName: string, timeoutMs: number): Promise<net.Socket> {
  const address = pipeName.startsWith("\\\\.\\pipe\\") ? pipeName : `\\\\.\\pipe\\${pipeName}`;
  const deadline = Date.now() + timeoutMs;
  while (true) {
    try {
      return await new Promise<net.Socket>((resolve, reject) => {
        const socket = net.createConnection(address);
        socket.once("connect", () => resolve(socket));
        socket.once("error", reject);
      });
    } catch (error) {
      if (Date.now() >= deadline) throw error;
      await new Promise((resolve) => setTimeout(resolve, 80));
    }
  }
}
