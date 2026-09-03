import net from "node:net";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { unlink } from "node:fs/promises";
import { isAuthenticationError, probeAntigravity, type AntigravityProbe } from "./antigravity.js";
import { AntigravityCoordinator, AntigravityInteractiveCoordinator, AntigravitySdkCoordinator, CoordinatorError, OpenAICompatibleCoordinator, deleteCoordinatorSessionFile, type CoordinatorProvider } from "./coordinator.js";
import { deleteAntigravitySdkSession } from "./antigravity-sdk.js";
import { PiWorkerBridge, type PiWorkerSettings } from "./pi-worker.js";
import { CodexAppServerBridge, CodexWorkerError, probeCodexAppServer, startCodexAppServerLogin, type CodexLoginHandle } from "./codex-worker.js";
import type { CodingWorker, CodingWorkerEvent } from "./coding-worker.js";
import { buildCompanionSystemPrompt, normalizeCompanionProfile } from "./companion.js";
import {
  isManagerClientMessage, normalizeManagerImageAttachments, normalizeStartConfig,
  type AntigravityTransport, type CodeResult, type CodingAgentConfig, type CompanionHistoryItem, type CompanionProfile, type MainAgentConfig, type ManagerAction, type ManagerClientMessage, type ManagerHostMessage, type ManagerImageAttachment,
} from "./protocol.js";
import { cleanupManagerRuntime, ensureManagerRuntime, type ManagerRuntime } from "./runtime.js";
import { createCodeTask, isExplicitCodingRequest, sanitizeCoordinatorInput } from "./policy.js";
import { buildCodingProgressPrompt, buildCodingResultPrompt, codingRelayEnabled, type CodingRelayItem } from "./coding-relay.js";

const inlinePipe = process.argv.find((arg) => arg.startsWith("--pipe="));
const pipeFlagIndex = process.argv.indexOf("--pipe");
const pipeArgument = inlinePipe?.slice("--pipe=".length) ?? (pipeFlagIndex >= 0 ? process.argv[pipeFlagIndex + 1] : undefined);
if (!pipeArgument) { console.error("Missing --pipe argument"); process.exit(2); }
const pipeName = pipeArgument.startsWith("\\\\.\\pipe\\") ? pipeArgument : `\\\\.\\pipe\\${pipeArgument}`;

type Send = (message: ManagerHostMessage) => void;
let activeSession: ManagerSession | undefined;
let activeSessionStart: Promise<void> | undefined;
let shuttingDown = false;
const codexLogins = new Map<string, CodexLoginHandle>();

const server = net.createServer((socket) => {
  socket.setEncoding("utf8");
  let buffer = "";
  const send: Send = (message) => socket.write(`${JSON.stringify(message)}\n`);
  send({ type: "manager_host_ready", version: "0.2.0" });
  socket.on("data", (chunk) => {
    buffer += chunk;
    let newline = buffer.indexOf("\n");
    while (newline >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      newline = buffer.indexOf("\n");
      if (!line) continue;
      try {
        const parsed = JSON.parse(line);
        if (!isManagerClientMessage(parsed)) throw new ManagerError("PROTOCOL_ERROR", "Invalid manager protocol message");
        void handle(parsed, send).catch((error) => sendError(send, parsed.sessionId, error));
      } catch (error) { sendError(send, undefined, error); }
    }
  });
  socket.on("close", () => {
    void activeSession?.dispose(); activeSession = undefined;
    for (const login of codexLogins.values()) void login.dispose();
    codexLogins.clear();
  });
});

async function handle(message: ManagerClientMessage, send: Send): Promise<void> {
  if (message.type === "shutdown") {
    if (shuttingDown) return;
    shuttingDown = true;
    await activeSession?.dispose(); activeSession = undefined;
    await Promise.all([...codexLogins.values()].map((login) => login.dispose())); codexLogins.clear();
    server.close(() => process.exit(0));
    return;
  }
  if (message.type === "probe_codex") {
    const status = await probeCodexAppServer(message.executable, message.workspacePath ?? process.cwd());
    send({ type: "codex_account_status", sessionId: message.sessionId, ...status });
    return;
  }
  if (message.type === "start_codex_login") {
    const previous = codexLogins.get(message.sessionId);
    if (previous) await previous.dispose();
    const login = await startCodexAppServerLogin(message.executable, message.workspacePath ?? process.cwd());
    codexLogins.set(message.sessionId, login);
    send({ type: "codex_login_started", sessionId: message.sessionId, loginId: login.loginId, url: login.authUrl });
    void login.completion.then(async (result) => {
      send({ type: "codex_login_completed", sessionId: message.sessionId, loginId: result.loginId ?? login.loginId, ok: result.ok, message: result.message });
      if (codexLogins.get(message.sessionId) === login) codexLogins.delete(message.sessionId);
      await login.dispose();
    });
    return;
  }
  if (message.type === "delete_manager_session") {
    if (activeSessionStart) await activeSessionStart;
    if (activeSession?.id === message.sessionId) {
      await activeSession.deletePersistent();
      await activeSession.dispose(); activeSession = undefined;
    } else {
      await deleteStoredBindings(message);
    }
    send({ type: "manager_session_deleted", sessionId: message.sessionId });
    return;
  }
  if (message.type === "start_manager_session") {
    if (activeSessionStart) { try { await activeSessionStart; } catch { } }
    if (activeSession?.id !== message.sessionId) { await activeSession?.dispose(); activeSession = undefined; }
    if (!activeSession) {
      activeSession = new ManagerSession(message, send);
      activeSessionStart = activeSession.start();
      try { await activeSessionStart; }
      catch (error) { await activeSession.dispose(); activeSession = undefined; throw error; }
      finally { activeSessionStart = undefined; }
    } else activeSession.emitReady();
    return;
  }
  if (activeSessionStart) await activeSessionStart;
  if (!activeSession || activeSession.id !== message.sessionId) throw new ManagerError("SESSION_NOT_FOUND", "Manager session is not active");
  if (message.type === "send_manager_message") await activeSession.prompt(message.text, message.attachments);
  if (message.type === "interactive_cli_response") activeSession.handleInteractiveCliResponse(message);
  if (message.type === "approve_coding_tool") activeSession.resolve(message.callId, message.approved);
  if (message.type === "resolve_coding_interaction") activeSession.resolve(message.requestId, message.approved, message.values);
  if (message.type === "cancel_manager_turn") activeSession.cancel();
}

class ManagerSession {
  readonly id: string;
  private runtime?: ManagerRuntime;
  private agyProbe: AntigravityProbe = { available: false, authenticated: false };
  private coordinator?: CoordinatorProvider;
  private worker?: CodingWorker;
  private busy = false;
  private cancelled = false;
  private activeTaskId?: string;
  private awaitingCodingDecision = false;
  private mainSessionRef?: string;
  private codingSessionRef?: string;
  private readonly companionProfile: CompanionProfile;
  private readonly conversationHistory: CompanionHistoryItem[];
  private readonly mainConfig: MainAgentConfig;
  private readonly codingConfig: CodingAgentConfig;
  private mainTransport: AntigravityTransport = "cli";
  private sdkSessionRef?: string;
  private legacyCliConversationId?: string;
  private narrationQueue: CodingRelayItem[] = [];
  private narrationRunning = false;
  private narrationStarted = false;
  private narrationClosed = false;
  private narrationGeneration = 0;

  constructor(private readonly config: Extract<ManagerClientMessage, { type: "start_manager_session" }>, private readonly send: Send) {
    this.id = config.sessionId;
    const normalized = normalizeStartConfig(config);
    this.mainConfig = normalized.mainAgent;
    this.codingConfig = normalized.codingAgent;
    this.companionProfile = normalizeCompanionProfile(config.companionProfile);
    this.conversationHistory = (config.conversationHistory ?? []).filter(item => item && (item.role === "user" || item.role === "assistant") && typeof item.text === "string").slice(-40);
    if (this.mainConfig.provider === "antigravity") {
      this.mainTransport = this.mainConfig.transport ?? "cli";
      this.sdkSessionRef = this.mainConfig.sdkSessionRef;
      this.legacyCliConversationId = this.mainConfig.legacyCliConversationId ?? this.mainConfig.conversationId;
      this.mainSessionRef = this.mainTransport === "sdk" ? this.sdkSessionRef : this.legacyCliConversationId;
    } else {
      this.mainSessionRef = this.mainConfig.sessionFile;
    }
    this.codingSessionRef = this.codingConfig.provider === "pi" ? this.codingConfig.sessionFile : this.codingConfig.threadId;
    this.awaitingCodingDecision = config.awaitingCodingDecision === true || config.awaitingPiDecision === true;
  }

  private get shouldRelayCoding(): boolean {
    return codingRelayEnabled(this.mainConfig.provider, this.codingConfig.provider);
  }

  private resetNarration(): void {
    this.narrationGeneration += 1;
    this.narrationQueue = [];
    this.narrationStarted = false;
    this.narrationClosed = false;
  }

  /** Queue a bounded, presentation-safe update for Antigravity. Updates are
   * serialized because AGY accepts one prompt per long-lived stream at a time.
   * Progress is intentionally lossy: stale intermediate updates may be
   * dropped while the coordinator is generating the previous sentence. */
  private queueNarration(item: CodingRelayItem): void {
    if (!this.shouldRelayCoding || this.cancelled || this.narrationClosed) return;
    if (item.final) {
      this.narrationQueue = [item];
    } else {
      const previous = this.narrationQueue.at(-1);
      if (previous && !previous.final && previous.prompt === item.prompt) return;
      this.narrationQueue.push(item);
      if (this.narrationQueue.length > 6) this.narrationQueue.splice(0, this.narrationQueue.length - 6);
    }
    void this.drainNarrationQueue(this.narrationGeneration);
  }

  private async drainNarrationQueue(generation: number): Promise<void> {
    if (this.narrationRunning || !this.shouldRelayCoding) return;
    this.narrationRunning = true;
    let processingFinal = false;
    try {
      while (this.narrationQueue.length > 0 && generation === this.narrationGeneration && !this.cancelled) {
        const item = this.narrationQueue.shift();
        if (!item) break;
        processingFinal = item.final;
        let emittedText = false;
        const coordinator = await this.ensureCoordinator();
        if (coordinator.source !== "antigravity" || !coordinator.narrate) return;
        if (!this.narrationStarted) {
          this.send({ type: "manager_thinking_delta", sessionId: this.id, source: "antigravity", text: "我来陪你处理这项任务…" });
          this.narrationStarted = true;
        }
        const turn = await coordinator.narrate(item.prompt, (event) => {
          if (generation !== this.narrationGeneration || this.cancelled || !event.text) return;
          if (event.kind === "thinking") this.send({ type: "manager_thinking_delta", sessionId: this.id, source: "antigravity", text: event.text });
          else { emittedText = true; this.send({ type: "manager_delta", sessionId: this.id, source: "antigravity", text: event.text }); }
        });
        if (generation !== this.narrationGeneration || this.cancelled) return;
        if (!emittedText && turn) {
          emittedText = true;
          this.send({ type: "manager_delta", sessionId: this.id, source: "antigravity", text: turn });
        }
        // Every narration turn is a complete, user-visible bubble. The final
        // flag distinguishes the last bubble of the coding task from an
        // intermediate progress bubble so the desktop can close the visual
        // bubble without marking the whole task idle too early.
        this.send({ type: "manager_completed", sessionId: this.id, source: "antigravity", text: "", action: "respond", final: item.final });
        if (item.final) {
          this.narrationClosed = true;
          this.setState("idle");
        }
        processingFinal = false;
      }
    } catch {
      // A narration failure must never fail or cancel the Coding Worker. A
      // final result still gets a neutral local fallback so the user is not
      // left with an unfinished “thinking” row.
      if (generation === this.narrationGeneration && !this.cancelled && (processingFinal || this.narrationQueue.some((item) => item.final))) {
        this.narrationQueue = [];
        this.narrationClosed = true;
        this.send({ type: "manager_delta", sessionId: this.id, source: "antigravity", text: "代码任务已经结束，详细变更和验证结果已列在下方。" });
        this.send({ type: "manager_completed", sessionId: this.id, source: "antigravity", text: "", action: "respond", final: true });
        this.setState("idle");
      }
    } finally {
      this.narrationRunning = false;
      // A new coding turn may have replaced the queue while the previous AGY
      // request was still finishing. Drain whatever belongs to the current
      // generation after releasing the running flag.
      if (this.narrationQueue.length > 0 && !this.cancelled) void this.drainNarrationQueue(this.narrationGeneration);
    }
  }

  async start(): Promise<void> {
    this.runtime = await ensureManagerRuntime(this.companionProfile);
    if (this.mainConfig.provider === "antigravity") {
      if (this.mainTransport === "sdk") {
        try {
          const coordinator = await this.ensureSdkCoordinator();
          this.agyProbe = { available: true, authenticated: true, version: coordinator.version };
          this.mainSessionRef = coordinator.sessionRef;
          this.sdkSessionRef = coordinator.sessionRef;
        } catch (error) {
          this.agyProbe = { available: false, authenticated: false, message: error instanceof Error ? error.message : "Antigravity SDK Bridge 启动失败。" };
        }
      } else {
        this.agyProbe = await probeAntigravity(this.mainConfig.executable?.trim() || "agy", this.runtime.root);
      }
      this.emitAntigravityStatus();
    } else {
      this.send({ type: "provider_status", sessionId: this.id, layer: "main", provider: "openai_compatible", available: Boolean(this.mainConfig.baseUrl && this.mainConfig.modelId), authenticated: Boolean(this.mainConfig.apiKey) });
    }
    if (this.codingConfig.provider === "codex") {
      // Probe the App Server during session activation so a missing Codex
      // login/CLI is visible before Antigravity delegates a task.  The probe
      // is informational; normal answering still works when the coding
      // provider is unavailable, while ensureWorker() returns the same
      // explicit error if a coding task is actually requested.
      try {
        const status = await probeCodexAppServer(this.codingConfig.executable, this.config.workspacePath);
        this.send({ type: "provider_status", sessionId: this.id, layer: "coding", provider: "codex", available: status.available, authenticated: status.authenticated, version: status.version, policy: status.policy, message: status.authenticated ? undefined : "Codex CLI 可用，但尚未登录。" });
      } catch (error) {
        this.send({ type: "provider_status", sessionId: this.id, layer: "coding", provider: "codex", available: false, authenticated: false, message: error instanceof Error ? error.message : "无法连接 Codex App Server。" });
      }
    } else {
      this.send({ type: "provider_status", sessionId: this.id, layer: "coding", provider: "pi", available: true, authenticated: Boolean(this.codingConfig.apiKey) });
    }
    this.emitReady(); this.setState("idle");
  }

  emitReady(): void {
    this.send({
      type: "manager_session_ready", sessionId: this.id,
      mainProvider: this.mainConfig.provider, codingProvider: this.codingConfig.provider,
      mainSessionRef: this.mainSessionRef, codingSessionRef: this.codingSessionRef,
      agyConversationId: this.mainConfig.provider === "antigravity" ? this.legacyCliConversationId : undefined,
      piSessionFile: this.codingConfig.provider === "pi" ? this.codingSessionRef : undefined,
      antigravityAvailable: this.mainConfig.provider === "antigravity" ? this.agyProbe.available : undefined,
      authenticated: this.mainConfig.provider === "antigravity" ? this.agyProbe.authenticated : Boolean(this.mainConfig.apiKey),
      version: this.mainConfig.provider === "antigravity" ? this.agyProbe.version : undefined,
      antigravityTransport: this.mainConfig.provider === "antigravity" ? this.mainTransport : undefined,
    });
  }

  async prompt(userMessage: string, attachments: ManagerImageAttachment[] = []): Promise<void> {
    attachments = normalizeManagerImageAttachments(attachments);
    this.validateManagedAttachments(attachments);
    if (this.busy) throw new ManagerError("BUSY", "Manager is already processing a turn");
    if (this.awaitingCodingDecision) {
      if (attachments.length > 0 && this.codingConfig.provider !== "codex") {
        throw new ManagerError("IMAGE_INPUT_UNSUPPORTED", "当前 Coding Agent 不支持图片输入，请在设置中选择 Codex。");
      }
      this.busy = true; this.cancelled = false; this.awaitingCodingDecision = false;
      try { await this.delegateToWorker(userMessage, undefined, attachments); }
      catch (error) { this.busy = false; this.setState("error"); throw error; }
      return;
    }
    if (isExplicitCodingRequest(userMessage)) {
      if (attachments.length > 0 && this.codingConfig.provider !== "codex") {
        throw new ManagerError("IMAGE_INPUT_UNSUPPORTED", "当前 Coding Agent 不支持图片输入，请在设置中选择 Codex。");
      }
      this.busy = true; this.cancelled = false;
      try { await this.delegateToWorker(userMessage, undefined, attachments); }
      catch (error) { this.busy = false; this.setState("error"); throw error; }
      return;
    }
    if (attachments.length > 0) {
      if (this.mainConfig.provider !== "antigravity") {
        throw new ManagerError("IMAGE_INPUT_UNSUPPORTED", "当前主 Agent 不支持图片陪伴输入，请切换到 Antigravity SDK。");
      }
      if (attachments.length > 1 && this.mainTransport !== "sdk") {
        throw new ManagerError("AGY_IMAGE_LIMIT", "Antigravity CLI 第一版每条消息只支持一张图片，请移除多余图片后重试。");
      }
      if (this.mainTransport === "cli") await this.migrateLegacyCliToInteractive();
    }
    this.busy = true; this.cancelled = false; this.setState("routing");
    try {
      const coordinator = await this.ensureCoordinator();
      // Keep the manager transcript responsive even though the coordinator
      // must still return one validated routing object before we delegate.
      // Antigravity's stream may contain native reasoning deltas; API
      // coordinators simply keep the placeholder until their response is
      // available.
      this.send({ type: "manager_thinking_delta", sessionId: this.id, source: coordinator.source, text: "正在分析…" });
      let streamedResponseText = false;
      const turn = await coordinator.ask(sanitizeCoordinatorInput(userMessage), (event) => {
        if (!event.text) return;
        if (event.kind === "text") {
          streamedResponseText = true;
          this.setState("responding");
        }
        this.send({
          type: event.kind === "thinking" ? "manager_thinking_delta" : "manager_delta",
          sessionId: this.id, source: coordinator.source, text: event.text,
        });
      }, attachments);
      this.mainSessionRef = turn.sessionRef ?? coordinator.sessionRef ?? this.mainSessionRef;
      this.emitReady();
      this.send({
        type: "manager_metrics", sessionId: this.id, provider: coordinator.source,
        contextTokens: turn.contextTokens, contextWindow: turn.contextWindow, cacheReadTokens: turn.cacheReadTokens,
        antigravityCacheReadTokens: coordinator.source === "antigravity" ? turn.cacheReadTokens : undefined,
      });
      if (this.cancelled) return;
      if (turn.action.action === "delegate_code") {
        // Close the manager's streamed thinking row before the coding worker
        // starts. Without this lifecycle event a placeholder can remain stuck
        // at “正在思考” while the Pi/Codex transcript is already running.
      this.send({ type: "manager_completed", sessionId: this.id, source: coordinator.source, text: "", action: "delegate_code", final: true });
        await this.delegateToWorker(userMessage, turn.action); return;
      }
      const text = turn.action.message;
      this.setState("responding");
      // Streaming providers have already delivered the response text through
      // manager_delta. Only use the completed action as a fallback for
      // providers that cannot expose a stream; otherwise the whole response
      // would be appended a second time after the live text.
      if (text && !streamedResponseText) this.send({ type: "manager_delta", sessionId: this.id, source: coordinator.source, text });
      this.send({ type: "manager_completed", sessionId: this.id, source: coordinator.source, text, action: turn.action.action, final: true });
      this.busy = false; this.setState("idle");
    } catch (error) {
      this.busy = false;
      if (this.cancelled) { this.setState("cancelled"); return; }
      const errorMessage = error instanceof Error ? error.message : "";
      const authenticationFailure = this.mainConfig.provider === "antigravity" &&
        (isAuthenticationError(errorMessage) || (error as { code?: unknown })?.code === "SDK_AUTH_REQUIRED");
      if (authenticationFailure) {
        this.agyProbe = { ...this.agyProbe, authenticated: false, authenticationRequired: true, message: error instanceof Error ? error.message : "Authentication required" };
        this.emitAntigravityStatus();
      }
      sendError(
        this.send,
        this.id,
        authenticationFailure
          ? new ManagerError("AGY_AUTH_REQUIRED", this.mainTransport === "sdk"
            ? "Antigravity SDK 尚未完成认证。请配置 SDK 使用的 Gemini API Key、Vertex/ADC 登录状态后重试。"
            : "Antigravity CLI 登录状态已失效。请在设置中打开登录终端完成登录后重试。")
          : error,
      );
      this.setState("error");
    }
  }

  resolve(requestId: string, approved: boolean, values?: Record<string, unknown>): void {
    if (!this.worker) throw new ManagerError("WORKER_NOT_STARTED", "Coding Worker is not active");
    this.worker.resolve(requestId, approved, values); this.setState("coding");
  }

  handleInteractiveCliResponse(message: Extract<ManagerClientMessage, { type: "interactive_cli_response" }>): void {
    const coordinator = this.coordinator;
    if (coordinator instanceof AntigravityInteractiveCoordinator) coordinator.handleResponse(message);
  }

  cancel(): void {
    this.cancelled = true; this.awaitingCodingDecision = false;
    const closeNarration = this.shouldRelayCoding && this.narrationStarted && !this.narrationClosed;
    this.narrationGeneration += 1; this.narrationQueue = [];
    if (closeNarration) {
      this.narrationClosed = true;
      this.send({ type: "manager_completed", sessionId: this.id, source: "antigravity", text: "", action: "respond", final: true });
    }
    this.coordinator?.cancel(); this.worker?.cancel();
    if (this.activeTaskId) {
      this.send({ type: "code_result", sessionId: this.id, taskId: this.activeTaskId, result: cancelledResult() });
      this.activeTaskId = undefined;
    }
    this.busy = false; this.setState("cancelled");
  }

  async deletePersistent(): Promise<void> {
    if (this.mainConfig.provider === "openai_compatible") await deleteCoordinatorSessionFile(this.mainSessionRef);
    if (this.mainConfig.provider === "antigravity" && this.mainTransport === "sdk") await deleteAntigravitySdkSession(this.sdkSessionRef ?? this.mainSessionRef);
    if (this.worker?.deleteSession) await this.worker.deleteSession();
    else if (this.codingConfig.provider === "pi") await deletePiSessionFile(this.codingSessionRef);
    else if (this.codingSessionRef) {
      const bridge = new CodexAppServerBridge({ sessionId: this.id, workspacePath: path.resolve(this.config.workspacePath), executable: this.codingConfig.executable, threadId: this.codingSessionRef }, () => undefined);
      try { await bridge.start(); await bridge.deleteSession(); } finally { await bridge.dispose(); }
    }
  }

  async dispose(): Promise<void> {
    this.cancel();
    await this.coordinator?.dispose(); this.coordinator = undefined;
    await this.worker?.dispose(); this.worker = undefined;
    await cleanupManagerRuntime(this.runtime); this.runtime = undefined;
  }

  private async ensureCoordinator(): Promise<CoordinatorProvider> {
    if (this.coordinator) return this.coordinator;
    if (!this.runtime) throw new ManagerError("MANAGER_NOT_READY", "Manager runtime is not initialized");
    if (this.mainConfig.provider === "antigravity") {
      if (this.mainTransport === "sdk") return this.ensureSdkCoordinator();
      if (!this.agyProbe.available) throw new ManagerError("AGY_NOT_FOUND", this.agyProbe.message || "Antigravity CLI was not found");
      if (!this.agyProbe.authenticated) {
        this.agyProbe = await probeAntigravity(this.mainConfig.executable?.trim() || "agy", this.runtime.root);
        this.emitAntigravityStatus(); this.emitReady();
        if (!this.agyProbe.available) throw new ManagerError("AGY_NOT_FOUND", this.agyProbe.message || "Antigravity CLI was not found");
        // Never start a hidden coordinator when the probe could not confirm an
        // authenticated CLI. In print/stream mode agy may otherwise fall back
        // to an interactive OAuth flow, leaving the Manager looking like it is
        // thinking forever because there is no visible terminal to complete
        // the login.
        if (!this.agyProbe.authenticated) {
          throw new ManagerError(
            "AGY_AUTH_REQUIRED",
            "Antigravity CLI 尚未确认可复用的登录状态。为避免隐藏进程进入交互式 OAuth，已停止本轮请求。请在设置中打开登录终端完成登录后，重启 Manager 或重试。",
          );
        }
      }
      if (this.mainTransport === "cli_interactive") {
        this.coordinator = new AntigravityInteractiveCoordinator({ ...this.mainConfig, transport: "cli_interactive", legacyCliConversationId: this.legacyCliConversationId ?? this.mainSessionRef }, this.runtime, this.id, this.send);
      } else {
        this.coordinator = new AntigravityCoordinator({ ...this.mainConfig, conversationId: this.mainSessionRef }, this.runtime);
      }
    } else {
      if (!this.mainConfig.baseUrl || !this.mainConfig.modelId) throw new ManagerError("MAIN_API_INVALID", "主 Agent API 的 Base URL 和模型不能为空。");
      this.coordinator = new OpenAICompatibleCoordinator(
        { ...this.mainConfig, sessionFile: this.mainSessionRef },
        this.id,
        buildCompanionSystemPrompt(this.companionProfile),
      );
    }
    return this.coordinator;
  }

  private async ensureSdkCoordinator(): Promise<AntigravitySdkCoordinator> {
    if (!this.runtime) throw new ManagerError("MANAGER_NOT_READY", "Manager runtime is not initialized");
    if (this.coordinator instanceof AntigravitySdkCoordinator) return this.coordinator;
    if (this.coordinator) { await this.coordinator.dispose(); this.coordinator = undefined; }
    const config = this.mainConfig.provider === "antigravity"
      ? { ...this.mainConfig, transport: "sdk" as const, sdkSessionRef: this.sdkSessionRef }
      : { provider: "antigravity" as const, transport: "sdk" as const, model: undefined, effort: undefined, timeoutSeconds: 120, sdkSessionRef: this.sdkSessionRef };
    const coordinator = new AntigravitySdkCoordinator(
      config,
      this.runtime,
      buildCompanionSystemPrompt(this.companionProfile),
      this.conversationHistory,
      this.id,
    );
    try {
      const probe = await coordinator.start();
      this.coordinator = coordinator;
      this.agyProbe = { available: probe.available, authenticated: probe.authenticated, version: probe.version, message: probe.message };
      this.sdkSessionRef = probe.sessionRef ?? coordinator.sessionRef;
      this.mainSessionRef = this.sdkSessionRef;
      return coordinator;
    } catch (error) {
      await coordinator.dispose().catch(() => undefined);
      throw error;
    }
  }

  private async migrateLegacyCliToInteractive(): Promise<void> {
    if (this.mainTransport === "cli_interactive" || this.mainTransport === "sdk") return;
    this.legacyCliConversationId ??= this.mainSessionRef;
    const previousTransport = this.mainTransport;
    const previousSessionRef = this.mainSessionRef;
    this.mainTransport = "cli_interactive";
    this.mainSessionRef = this.legacyCliConversationId;
    try {
      if (this.coordinator) {
        await this.coordinator.dispose();
        this.coordinator = undefined;
      }
      // Do not persist the new transport before the first interactive turn
      // succeeds. The normal prompt completion path emits manager_session_ready
      // with the new binding after the turn has actually completed.
      await this.ensureCoordinator();
    } catch (error) {
      const failedCoordinator = this.coordinator;
      this.coordinator = undefined;
      try { await failedCoordinator?.dispose(); } catch { }
      this.mainTransport = previousTransport;
      this.mainSessionRef = previousSessionRef;
      throw error;
    }
  }

  private validateManagedAttachments(attachments: readonly ManagerImageAttachment[]): void {
    if (attachments.length === 0) return;
    if (!/^[A-Za-z0-9_-]+$/.test(this.id)) throw new ManagerError("IMAGE_ATTACHMENT_UNMANAGED", "会话标识无效，无法使用图片附件。");
    const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
    const root = path.resolve(localAppData, "IlMatto", "manager-sessions", "attachments", this.id);
    for (const attachment of attachments) {
      const candidate = path.resolve(attachment.path);
      const relative = path.relative(root, candidate);
      if (!relative || relative.startsWith("..") || path.isAbsolute(relative))
        throw new ManagerError("IMAGE_ATTACHMENT_UNMANAGED", "图片必须先由 IlMatto 保存到当前会话的受管附件目录。");
    }
  }

  private async ensureWorker(): Promise<CodingWorker> {
    if (this.worker) return this.worker;
    const workspace = path.resolve(this.config.workspacePath);
    if (this.codingConfig.provider === "pi") {
      const settings: PiWorkerSettings = {
        sessionId: this.id, workspacePath: workspace, baseUrl: this.codingConfig.baseUrl,
        modelId: this.codingConfig.modelId, apiKey: this.codingConfig.apiKey, sessionFile: this.codingSessionRef,
        autoApproveSafeCommands: this.codingConfig.autoApproveSafeCommands ?? false,
        autoApproveGitOperations: this.codingConfig.autoApproveGitOperations ?? false,
      };
      this.worker = new PiWorkerBridge(settings, (event) => this.onWorkerEvent(event));
    } else {
      this.worker = new CodexAppServerBridge({
        sessionId: this.id, workspacePath: workspace, executable: this.codingConfig.executable,
        threadId: this.codingSessionRef, model: this.codingConfig.model, effort: this.codingConfig.effort,
      }, (event) => this.onWorkerEvent(event));
    }
    try {
      await this.worker.start();
      return this.worker;
    } catch (error) {
      await this.worker.dispose().catch(() => undefined);
      this.worker = undefined;
      throw error;
    }
  }

  private onWorkerEvent(event: CodingWorkerEvent): void {
    if (event?.sessionId && event.sessionId !== this.id) return;
    const taskId = this.activeTaskId;
    const source = this.codingConfig.provider;
    if (this.shouldRelayCoding && event?.type !== "code_result") {
      const narrationPrompt = buildCodingProgressPrompt(event);
      if (narrationPrompt) this.queueNarration({ final: false, prompt: narrationPrompt });
    }
    switch (event?.type) {
      case "session_ready":
        this.codingSessionRef = event.sessionFile ?? event.sessionRef ?? event.threadId ?? this.codingSessionRef;
        this.emitReady(); break;
      case "provider_status":
        this.send({ type: "provider_status", sessionId: this.id, layer: "coding", provider: source, available: event.available !== false, authenticated: event.authenticated, version: event.version, message: event.message, policy: event.policy }); break;
      case "assistant_delta":
        if (!this.shouldRelayCoding) this.send({ type: "coding_delta", sessionId: this.id, taskId, source, text: event.text ?? "" });
        break;
      case "thinking_delta":
        if (!this.shouldRelayCoding) this.send({ type: "coding_thinking_delta", sessionId: this.id, taskId, source, text: event.text ?? "" });
        break;
      case "tool_approval_request":
        this.send({ type: "coding_tool_approval_request", sessionId: this.id, callId: event.callId, tool: event.tool, summary: event.summary ?? event.tool, details: event.details ?? "", diff: event.diff });
        this.setState("waiting_approval"); break;
      case "interaction_request":
        this.send({ type: "coding_interaction_request", sessionId: this.id, requestId: event.requestId, provider: source, kind: event.kind, title: event.title, details: event.details ?? "", diff: event.diff, fields: event.fields, url: event.url });
        this.setState("waiting_approval"); break;
      case "interaction_completed":
        this.send({ type: "coding_interaction_completed", sessionId: this.id, requestId: event.requestId, provider: source }); break;
      case "tool_started": this.send({ type: "coding_tool_started", sessionId: this.id, callId: event.callId, tool: event.tool, command: event.command }); break;
      case "tool_output": this.send({ type: "coding_tool_output", sessionId: this.id, callId: event.callId, tool: event.tool, text: event.text ?? "" }); break;
      case "tool_completed": this.send({ type: "coding_tool_completed", sessionId: this.id, callId: event.callId, tool: event.tool, ok: Boolean(event.ok), summary: event.summary ?? "", output: event.output, diff: event.diff, autoApproved: event.autoApproved }); break;
      case "code_result":
        if (this.shouldRelayCoding && event.result) {
          this.queueNarration({ final: true, prompt: buildCodingResultPrompt(event.result as CodeResult) });
          this.setState("responding");
        }
        this.send({ type: "code_result", sessionId: this.id, taskId: event.taskId, result: event.result });
        this.awaitingCodingDecision = event.result?.status === "blocked" || event.result?.needsUserDecision === true;
        this.activeTaskId = undefined; this.busy = false;
        if (!this.shouldRelayCoding || !event.result) this.setState(event.result?.status === "failed" ? "error" : "idle");
        break;
      case "error": this.send({ type: "manager_error", sessionId: this.id, code: event.code ?? "WORKER_ERROR", message: event.message ?? `${source} Worker failed` }); break;
    }
  }

  private setState(state: Extract<ManagerHostMessage, { type: "manager_state" }>["state"]): void { this.send({ type: "manager_state", sessionId: this.id, state }); }

  private emitAntigravityStatus(): void {
    const message = this.agyProbe.message ? summarizeProbeMessage(this.agyProbe.message) : undefined;
    this.send({ type: "antigravity_status", sessionId: this.id, available: this.agyProbe.available, authenticated: this.agyProbe.authenticated, version: this.agyProbe.version, message });
    this.send({ type: "provider_status", sessionId: this.id, layer: "main", provider: "antigravity", available: this.agyProbe.available, authenticated: this.agyProbe.authenticated, version: this.agyProbe.version, message });
  }

  private async delegateToWorker(userMessage: string, action: ManagerAction = { schemaVersion: 1, action: "delegate_code", message: "" }, attachments: ManagerImageAttachment[] = []): Promise<void> {
    this.resetNarration();
    const taskId = crypto.randomUUID(); this.activeTaskId = taskId;
    this.setState("coding"); this.send({ type: "delegation_started", sessionId: this.id, taskId, provider: this.codingConfig.provider });
    try {
      const worker = await this.ensureWorker();
      const codeTask = createCodeTask(taskId, userMessage, action);
      worker.sendCodeTask(codeTask.taskId, codeTask.userRequest, attachments);
      this.queueNarration({ final: false, prompt: buildCodingProgressPrompt({ type: "delegation_started" }) ?? "" });
    } catch (error) {
      this.activeTaskId = undefined; this.busy = false;
      throw error;
    }
  }
}

class ManagerError extends Error { constructor(readonly code: string, message: string) { super(message); } }

function summarizeProbeMessage(value: string): string {
  const lines = value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const relevant = lines.find((line) => /not logged in|sign in|login|required|credential|keyring|access denied|error/i.test(line));
  return (relevant ?? lines.at(-1) ?? value).slice(0, 800);
}

function sendError(send: Send, sessionId: string | undefined, error: unknown): void {
  const code = error instanceof ManagerError || error instanceof CoordinatorError || error instanceof CodexWorkerError || (typeof (error as any)?.code === "string") ? (error as any).code : "MANAGER_ERROR";
  send({ type: "manager_error", sessionId, code, message: error instanceof Error ? error.message : "Manager request failed" });
}

async function deleteStoredBindings(message: Extract<ManagerClientMessage, { type: "delete_manager_session" }>): Promise<void> {
  await deleteCoordinatorSessionFile(message.coordinatorSessionFile ?? (message.mainAgent?.provider === "openai_compatible" ? message.mainAgent.sessionFile : undefined));
  if (message.mainAgent?.provider === "antigravity" && (message.mainAgent.transport ?? "cli") === "sdk")
    await deleteAntigravitySdkSession(message.mainAgent.sdkSessionRef);
  await deletePiSessionFile(message.piSessionFile ?? (message.codingAgent?.provider === "pi" ? message.codingAgent.sessionFile : undefined));
  const codex = message.codexThreadId ?? (message.codingAgent?.provider === "codex" ? message.codingAgent.threadId : undefined);
  if (codex && message.codingAgent?.provider === "codex") {
    const bridge = new CodexAppServerBridge({ sessionId: message.sessionId, workspacePath: path.resolve(message.workspacePath ?? process.cwd()), executable: message.codingAgent.executable, threadId: codex }, () => undefined);
    try { await bridge.start(); await bridge.deleteSession(); } finally { await bridge.dispose(); }
  }
}

async function deletePiSessionFile(value: string | undefined): Promise<void> {
  if (!value) return;
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  const directory = path.resolve(process.env.ILMATTO_SESSION_DIR ?? path.join(localAppData, "IlMatto", "sessions"));
  const file = path.resolve(value);
  if (path.dirname(file).toLowerCase() !== directory.toLowerCase() || path.extname(file).toLowerCase() !== ".jsonl") throw new ManagerError("SESSION_DELETE_INVALID", "Pi session file is outside the IlMatto sessions directory");
  try { await unlink(file); } catch (error: any) { if (error?.code !== "ENOENT") throw new ManagerError("SESSION_DELETE_FAILED", error instanceof Error ? error.message : "Unable to delete Pi session"); }
}

function cancelledResult() { return { status: "cancelled" as const, summaryForUser: "代码任务已取消。", technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false }; }

server.listen(pipeName);
