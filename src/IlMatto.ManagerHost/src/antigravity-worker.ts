import { randomUUID } from "node:crypto";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import type { CodingWorker } from "./coding-worker.js";
import { codeResultSchema, type AntigravityExecutionPolicy, type CodeResult, type ManagerImageAttachment, validateCodeResult } from "./protocol.js";

export type AntigravityWorkerSettings = {
  sessionId: string;
  workspacePath: string;
  executable?: string;
  model?: string;
  effort?: "low" | "medium" | "high";
  executionPolicy: AntigravityExecutionPolicy;
  mode?: "implementation" | "verification";
};

/**
 * A separately generated AGY agent for code work.  It intentionally does not
 * reuse the companion runtime or its conversation: capability escalation in
 * a coding task can never change the companion agent's tool policy.
 */
export class AntigravityWorkerBridge implements CodingWorker {
  readonly provider = "antigravity" as const;
  private child?: ChildProcessWithoutNullStreams;
  private buffer = "";
  private stderr = "";
  private runtime?: ExecutorRuntime;
  private activeTaskId?: string;
  private completed = false;
  private agentFailureReported = false;
  private agentFallbackRetries = 0;
  private restarting = false;
  private retryTimer?: NodeJS.Timeout;
  private logWatchTimer?: NodeJS.Timeout;
  private logReadInFlight = false;
  private logSeenText = "";
  private activeTaskInstruction?: string;
  private spawnConfig?: { executable: string; args: string[] };
  private taskTimer?: NodeJS.Timeout;
  private readonly startedToolCalls = new Set<string>();
  private readonly completedToolCalls = new Set<string>();

  constructor(private readonly settings: AntigravityWorkerSettings, private readonly onEvent: (event: any) => void) {}

  get sessionRef(): string | undefined { return undefined; }

  async start(): Promise<void> {
    if (this.child) return;
    this.runtime = await ensureExecutorRuntime(this.settings);
    this.agentFailureReported = false;
    this.agentFallbackRetries = 0;
    this.restarting = false;
    const args = [
      "--agent", this.runtime.agentName,
      "--input-format", "stream-json",
      "--output-format", "stream-json",
      "--json-schema", this.runtime.schemaPath,
      "--sandbox",
      "--log-file", this.runtime.logPath,
    ];
    // `accept-edits` is deliberately reserved for an explicit autonomy
    // choice.  In headless mode AGY cannot surface an interactive diff review
    // back to IlMatto, so the other profiles keep AGY's review policy intact.
    if (this.settings.mode !== "verification" && this.settings.executionPolicy === "autonomous") {
      args.push("--mode", "accept-edits");
    }
    if (this.settings.model?.trim()) args.push("--model", this.settings.model.trim());
    if (this.settings.effort) args.push("--effort", this.settings.effort);
    const executable = this.settings.executable?.trim() || "agy";
    this.spawnConfig = { executable, args };
    this.spawnChild();
    this.startLogWatcher();
    this.onEvent({ type: "session_ready", sessionId: this.settings.sessionId });
  }

  sendCodeTask(taskId: string, userRequest: string, _attachments?: ManagerImageAttachment[]): void {
    if (this.agentFailureReported) throw new Error("Antigravity 编码 agent 未加载，已停止本次任务。");
    if (this.activeTaskId) throw new Error("Antigravity 编码执行器正在处理另一项任务。");
    this.activeTaskId = taskId; this.completed = false;
    this.startedToolCalls.clear(); this.completedToolCalls.clear();
    this.taskTimer = setTimeout(() => {
      if (!this.activeTaskId || this.completed) return;
      this.fail("AGY_EXECUTOR_TIMEOUT", "Antigravity 编码任务超过 15 分钟仍未返回结构化结果。请检查登录状态、权限规则和执行器日志。");
      try { this.child?.kill(); } catch { }
    }, 15 * 60 * 1000);
    const mode = this.settings.mode === "verification" ? "验证" : "实现";
    const instruction = [
      `你是 IlMatto 的独立 Antigravity ${mode}执行器。`,
      "仅在当前工作区内工作。不得访问或发送其他 Agent 的会话、推理、命令日志或私有执行轨迹。",
      this.settings.mode === "verification"
        ? "这是验证任务：不得修改源工作区文件、不得提交 Git；仅运行用户要求或项目正常测试，并在最终结果中报告验证。"
        : executionPolicyInstruction(this.settings.executionPolicy),
      this.settings.mode === "verification"
        ? "该验证由用户在 Codex 任务成功完成后显式启动。"
        : "除非用户的任务明确要求编译、测试或验证，否则不要自行运行测试；不得把自己作为其他 Agent 的后台验证者。",
      "完成后必须只输出符合给定 JSON Schema 的 CodeResult。",
      "用户任务：",
      userRequest,
    ].join("\n\n");
    this.activeTaskInstruction = instruction;
    if (this.restarting) return;
    if (!this.child?.stdin.writable) {
      this.activeTaskId = undefined;
      this.activeTaskInstruction = undefined;
      if (this.taskTimer) { clearTimeout(this.taskTimer); this.taskTimer = undefined; }
      throw new Error("Antigravity 编码执行器未启动。");
    }
    this.child.stdin.write(`${JSON.stringify({ event: "user", message: { content: instruction } })}\n`, "utf8");
  }

  resolve(_requestId: string, _approved: boolean, _values?: Record<string, unknown>): void {
    // AGY native approvals remain inside its own CLI permission boundary. The
    // host still records the visible tool lifecycle, but never forwards an
    // approval request to another agent.
  }

  cancel(): void {
    if (this.taskTimer) { clearTimeout(this.taskTimer); this.taskTimer = undefined; }
    if (this.retryTimer) { clearTimeout(this.retryTimer); this.retryTimer = undefined; }
    if (this.logWatchTimer) { clearInterval(this.logWatchTimer); this.logWatchTimer = undefined; }
    this.logReadInFlight = false;
    this.logSeenText = "";
    this.restarting = false;
    this.activeTaskInstruction = undefined;
    const taskId = this.activeTaskId;
    if (taskId && !this.completed) {
      this.completed = true;
      this.onEvent({ type: "code_result", sessionId: this.settings.sessionId, taskId, result: cancelledResult() });
    }
    try { this.child?.kill(); } catch { }
  }

  async dispose(): Promise<void> {
    this.cancel();
    const child = this.child; this.child = undefined;
    if (child && !child.killed) {
      try { child.stdin.end(); child.kill(); } catch { }
    }
    this.spawnConfig = undefined;
    await cleanupExecutorRuntime(this.runtime); this.runtime = undefined;
  }

  /** AGY writes agent-fallback diagnostics to --log-file rather than stderr. */
  private startLogWatcher(): void {
    if (this.logWatchTimer || !this.runtime) return;
    this.logWatchTimer = setInterval(() => {
      if (this.logReadInFlight || !this.runtime || this.agentFailureReported) return;
      this.logReadInFlight = true;
      void readFile(this.runtime.logPath, "utf8").then((text) => {
        const previous = this.logSeenText;
        this.logSeenText = text;
        // A retry may truncate/recreate the same log file. Ignore old text,
        // but inspect only lines appended since the last scan.
        const fresh = text.length >= previous.length && text.startsWith(previous) ? text.slice(previous.length) : text;
        if (/agent\s+["'`].*?["'`]\s+not found[\s\S]*falling back/i.test(fresh)) this.handleAgentFallback();
      }).catch(() => undefined).finally(() => { this.logReadInFlight = false; });
    }, 100);
    this.logWatchTimer.unref();
  }

  /** Spawn (or respawn) the isolated AGY process using the exact same args. */
  private spawnChild(): void {
    const config = this.spawnConfig;
    if (!config) return;
    const child = spawn(config.executable, config.args, {
      cwd: path.resolve(this.settings.workspacePath), windowsHide: true, stdio: ["pipe", "pipe", "pipe"],
    });
    this.child = child;
    child.stdout.setEncoding("utf8"); child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => this.onStdout(chunk));
    child.stderr.on("data", (chunk: string) => {
      this.stderr = `${this.stderr}${chunk}`.slice(-32_000);
      // AGY deliberately falls back to the default agent when a generated
      // custom agent is invalid or not discoverable. Retry once because AGY
      // can race its first custom-agent scan after a definition is created;
      // never let the default agent continue as a coding worker.
      if (/agent\s+["'`].*?["'`]\s+not found[\s\S]*falling back/i.test(this.stderr)) {
        this.handleAgentFallback();
      }
    });
    child.on("error", (error) => {
      if (this.child === child) this.fail("AGY_EXECUTOR_NOT_FOUND", error.message);
    });
    child.on("exit", (code) => {
      if (this.child !== child || this.restarting) return;
      if (!this.completed && this.activeTaskId) this.fail("AGY_EXECUTOR_EXITED", this.stderr || `Antigravity 执行器退出（${code ?? "未知"}）。`);
    });
    if (this.activeTaskInstruction && this.activeTaskId && child.stdin.writable) {
      child.stdin.write(`${JSON.stringify({ event: "user", message: { content: this.activeTaskInstruction } })}\n`, "utf8");
    }
  }

  private onStdout(chunk: string): void {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1); newline = this.buffer.indexOf("\n");
      if (!line) continue;
      try { this.onStreamEvent(JSON.parse(line)); } catch { /* ignore non-NDJSON diagnostics */ }
    }
  }

  private onStreamEvent(event: any): void {
    const eventName = String(event?.event ?? event?.type ?? "").toLowerCase();
    if (eventName === "init") {
      // AGY echoes the selected agent in the init envelope when --agent is
      // honored. Treat a missing or different value as a fallback too: a
      // default agent must never continue as a coding worker.
      const reportedAgent = event?.init?.agent ?? event?.data?.init?.agent ?? event?.agent;
      if (this.runtime && (typeof reportedAgent !== "string" || reportedAgent.trim() !== this.runtime.agentName)) {
        const actual = typeof reportedAgent === "string" && reportedAgent.trim() ? `「${reportedAgent.trim()}」` : "未返回 agent 名称";
        this.handleAgentFallback(`Antigravity 选择了 ${actual}，而不是 IlMatto 要求的「${this.runtime.agentName}」。`);
      }
      return;
    }
    const taskId = this.activeTaskId;
    if (!taskId) return;
    if (eventName === "step_update") {
      this.onStepUpdate(extractAntigravityWorkerStep(event));
      return;
    }
    if (eventName === "result") {
      const envelope = event?.result ?? event?.data?.result ?? {};
      const result = parseAntigravityWorkerResult(event);
      if (result) this.finish(result);
      else this.finish(failedResult(String(envelope?.error ?? "Antigravity 返回了无法识别的结构化结果。")));
      return;
    }
    // Keep compatibility with older wrappers that forward the payload without
    // the documented event envelope.
    this.onStepUpdate(extractAntigravityWorkerStep(event) ?? event);
    const result = parseAntigravityWorkerResult(event);
    if (result) this.finish(result);
  }

  private onStepUpdate(step: any): void {
    if (!step || typeof step !== "object") return;
    const taskId = this.activeTaskId;
    if (!taskId) return;
    const thinking = step?.thinking_delta ?? step?.reasoning_delta ?? step?.thought_delta;
    if (typeof thinking === "string" && thinking) this.onEvent({ type: "thinking_delta", sessionId: this.settings.sessionId, taskId, text: thinking });
    const text = step?.text_delta;
    if (typeof text === "string" && text && !text.trimStart().startsWith("{")) this.onEvent({ type: "assistant_delta", sessionId: this.settings.sessionId, taskId, text });
    const toolName = step?.tool_name ?? step?.tool_info?.name ?? step?.tool_info?.tool_name;
    if (typeof toolName === "string") {
      const callId = String(step?.call_id ?? step?.tool_call_id ?? step?.step_id ?? `${toolName}-${Date.now()}`);
      const command = commandFromStep(step);
      if (!this.startedToolCalls.has(callId)) {
        this.startedToolCalls.add(callId);
        this.onEvent({ type: "tool_started", sessionId: this.settings.sessionId, taskId, callId, tool: toolName, command });
      }
      const output = typeof step?.tool_info?.output === "string" ? step.tool_info.output
        : typeof step?.tool_output === "string" ? step.tool_output
        : typeof step?.output === "string" ? step.output : "";
      if (output) this.onEvent({ type: "tool_output", sessionId: this.settings.sessionId, taskId, callId, tool: toolName, text: output });
      const toolInfoError = step?.tool_info?.error ?? step?.error;
      const done = String(step?.state ?? step?.status ?? "").toUpperCase() === "DONE" || step?.completed === true;
      if (done && !this.completedToolCalls.has(callId)) {
        this.completedToolCalls.add(callId);
        this.onEvent({ type: "tool_completed", sessionId: this.settings.sessionId, taskId, callId, tool: toolName, ok: !toolInfoError, summary: toolInfoError ? `${toolName} 执行失败` : `${toolName} 已完成`, output });
      }
    }
  }

  private fail(code: string, message: string): void {
    const taskId = this.activeTaskId;
    this.onEvent({ type: "error", sessionId: this.settings.sessionId, code, message });
    if (taskId && !this.completed) this.finish(failedResult(message));
  }

  private reportAgentFailure(message: string, killProcess: boolean): void {
    if (this.agentFailureReported) return;
    this.agentFailureReported = true;
    this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "AGY_EXECUTOR_AGENT_NOT_LOADED", message });
    if (this.activeTaskId && !this.completed) this.finish(failedResult(message));
    if (killProcess) {
      try { this.child?.kill(); } catch { }
    }
  }

  private handleAgentFallback(message = "Antigravity 未加载 IlMatto 的编码 agent，而是回退到了默认 agent。请检查生成的 agent 定义和执行器日志。"): void {
    if (this.agentFailureReported || this.restarting) return;
    const config = this.spawnConfig;
    if (!config || this.agentFallbackRetries >= 1) {
      this.reportAgentFailure(message, true);
      return;
    }
    this.agentFallbackRetries += 1;
    this.restarting = true;
    const stale = this.child;
    this.child = undefined;
    try { stale?.kill(); } catch { }
    // Keep the generated definition and retry with a fresh CLI process. The
    // first process can miss a just-created custom agent in AGY's catalogue;
    // a second process is still isolated and uses the same schema/policy.
    this.retryTimer = setTimeout(() => {
      this.retryTimer = undefined;
      if (this.completed || this.agentFailureReported) return;
      this.restarting = false;
      this.stderr = "";
      this.spawnChild();
    }, 1000);
  }

  private finish(result: CodeResult): void {
    if (this.completed || !this.activeTaskId) return;
    if (this.taskTimer) { clearTimeout(this.taskTimer); this.taskTimer = undefined; }
    this.completed = true;
    this.activeTaskInstruction = undefined;
    this.onEvent({ type: "code_result", sessionId: this.settings.sessionId, taskId: this.activeTaskId, result });
    this.activeTaskId = undefined;
  }
}

type ExecutorRuntime = {
  root: string;
  agentName: string;
  agentPath: string;
  nestedAgentPath: string;
  workspaceAgentPath: string;
  workspaceNestedAgentPath: string;
  schemaPath: string;
  logPath: string;
};

async function ensureExecutorRuntime(settings: AntigravityWorkerSettings): Promise<ExecutorRuntime> {
  const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
  // Use a per-start nonce rather than a deterministic workspace/session key.
  // If a previous headless AGY process was killed outside the host, Windows
  // can keep its schema/log or agent files open; reusing that name makes AGY
  // resolve a stale definition (or report the agent as missing). A fresh
  // name also keeps concurrent sessions from colliding.
  const runtimeNonce = randomUUID().replace(/-/g, "").slice(0, 12);
  const root = path.join(localAppData, "IlMatto", "antigravity-executor", runtimeNonce);
  const globalAgentsRoot = path.resolve(process.env.ILMATTO_MANAGER_AGENT_DIR ?? path.join(os.homedir(), ".gemini", "config", "agents"));
  const workspaceAgentsRoot = path.join(path.resolve(settings.workspacePath), ".agents", "agents");
  const agentName = `ilmatto-executor-${path.basename(root)}`;
  const agentPath = path.join(globalAgentsRoot, `${agentName}.md`);
  const nestedAgentPath = path.join(globalAgentsRoot, agentName, "agent.md");
  const workspaceAgentPath = path.join(workspaceAgentsRoot, `${agentName}.md`);
  const workspaceNestedAgentPath = path.join(workspaceAgentsRoot, agentName, "agent.md");
  const schemaPath = path.join(root, "code-result.schema.json");
  const logPath = path.join(root, "antigravity-executor.log");
  // These are the names in AGY CLI's custom-agent tool catalogue. The
  // permission settings use read_file/write_file, but those are resource-rule
  // names rather than callable tool names; putting them in frontmatter makes
  // the agent invalid and silently triggers a default-agent fallback.
  const definition = renderExecutorAgent(agentName, settings);
  await mkdir(root, { recursive: true });
  // Workspace-local discovery is the primary path: it follows AGY's documented
  // `.agents/agents/<name>/agent.md` convention and works even when a stale
  // global AGY process has a handle open in ~/.gemini/config/agents.
  await mkdir(path.dirname(workspaceNestedAgentPath), { recursive: true });
  await writeFile(schemaPath, JSON.stringify(codeResultSchema, null, 2), "utf8");
  await writeFile(workspaceAgentPath, definition, "utf8");
  await writeFile(workspaceNestedAgentPath, definition, "utf8");
  // Keep the global copy for AGY versions that do not scan workspace agents,
  // but never make a locked/permission-restricted global catalogue fatal.
  try {
    await mkdir(path.dirname(nestedAgentPath), { recursive: true });
    await writeFile(agentPath, definition, "utf8");
    await writeFile(nestedAgentPath, definition, "utf8");
  } catch {
    // The workspace-local definition above is sufficient for supported AGY
    // versions; the fallback is intentionally best-effort.
  }
  return { root, agentName, agentPath, nestedAgentPath, workspaceAgentPath, workspaceNestedAgentPath, schemaPath, logPath };
}

/** Render the exact frontmatter written to AGY's custom-agent catalogue. */
export function renderExecutorAgent(agentName: string, settings: AntigravityWorkerSettings): string {
  const verification = settings.mode === "verification";
  const tools = verification
    ? ["view_file", "grep_search", "run_command"]
    : ["view_file", "grep_search", "write_to_file", "replace_file_content", "run_command"];
  return `---\nname: ${agentName}\ndescription: IlMatto isolated ${verification ? "verification" : "coding"} executor.\ntools:\n${tools.map((tool) => `  - ${tool}`).join("\n")}\nmainAgent: false\nsubagent: true\ncommandExecutionPolicy: ${commandExecutionPolicy(settings)}\nskills: []\nplugins: []\n---\n\n# IlMatto Executor Policy\n\n${verification ? "Verification only. Never edit source files." : executionPolicyInstruction(settings.executionPolicy)}\n`;
}

async function cleanupExecutorRuntime(runtime: ExecutorRuntime | undefined): Promise<void> {
  if (!runtime) return;
  await Promise.all([
    rm(runtime.agentPath, { force: true }).catch(() => undefined),
    rm(path.dirname(runtime.nestedAgentPath), { recursive: true, force: true }).catch(() => undefined),
    rm(runtime.workspaceAgentPath, { force: true }).catch(() => undefined),
    rm(path.dirname(runtime.workspaceNestedAgentPath), { recursive: true, force: true }).catch(() => undefined),
  ]);
}

function executionPolicyInstruction(policy: AntigravityExecutionPolicy): string {
  switch (policy) {
    case "safe_tests": return "允许在当前工作区内修改文件以完成用户的编码任务；仅当用户明确要求时运行项目常规测试。不得执行 Git、网络、MCP 或破坏性操作；如果命令不是常规测试则停止并报告。";
    case "autonomous": return "可在工作区内自主实现；仅在用户明确要求时运行编译或测试。不得访问工作区外路径、网络或 MCP，不得执行破坏性 Git 操作。";
    default: return "每次修改文件、执行命令、Git、网络或 MCP 操作前都必须请求用户确认。";
  }
}

/**
 * Custom-agent frontmatter uses its own policy enum (`off`, `auto`, `eager`,
 * `sandbox`); the CLI's `request-review`/`always-proceed` settings are not
 * valid values here. Safe tests and verification use `auto` so normal test
 * commands can run headlessly, while the explicit autonomous profile opts
 * into eager execution.
 */
export function commandExecutionPolicy(settings: AntigravityWorkerSettings): "off" | "auto" | "eager" {
  if (settings.mode === "verification") return "auto";
  switch (settings.executionPolicy) {
    case "autonomous": return "eager";
    case "safe_tests": return "auto";
    default: return "off";
  }
}

function parseResult(value: any): CodeResult | undefined {
  const candidates = [value?.structured_output, value?.structuredOutput, value?.response, value];
  for (const candidate of candidates) {
    const parsed = typeof candidate === "string" ? parseJson(candidate) : candidate;
    const result = validateCodeResult(parsed);
    if (result) return result;
  }
  return undefined;
}

/** Normalize the documented AGY stream envelope for regression tests and
 * compatibility with older wrappers that used `step` instead. */
export function extractAntigravityWorkerStep(event: any): any | undefined {
  return event?.step_update ?? event?.step ?? event?.data?.step_update ?? event?.data?.step;
}

/** Read a CodeResult from the terminal `result` event's structured output. */
export function parseAntigravityWorkerResult(event: any): CodeResult | undefined {
  return parseResult(event?.result ?? event?.data?.result ?? event);
}

function parseJson(value: string): unknown {
  const text = value.trim().replace(/^```(?:json)?\s*/i, "").replace(/\s*```$/, "");
  try { return JSON.parse(text); } catch { return undefined; }
}

function commandFromStep(step: any): string | undefined {
  const parameters = step?.tool_info?.parameters ?? step?.parameters;
  const value = parameters?.CommandLine ?? parameters?.commandLine ?? parameters?.command ?? parameters?.cmd ?? step?.command;
  return Array.isArray(value) ? value.map(String).join(" ") : typeof value === "string" ? value : undefined;
}

function failedResult(message: string): CodeResult { return { status: "failed", summaryForUser: message, technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false }; }
function cancelledResult(): CodeResult { return { status: "cancelled", summaryForUser: "代码任务已取消。", technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false }; }
