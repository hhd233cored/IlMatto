import net from "node:net";
import process from "node:process";
import { randomUUID } from "node:crypto";
import type { AgentToolRequest, CompanionMemoryOperation, CompanionMemoryRequest, CodexObservationOperation, ManagerClientMessage } from "./protocol.js";

type JsonRpcMessage = { jsonrpc?: string; id?: string | number | null; method?: string; params?: any; result?: any; error?: any };

const pipeArg = argument("--pipe") ?? process.env.ILMATTO_MANAGER_PIPE;
const sessionId = argument("--session-id") ?? process.env.ILMATTO_MANAGER_SESSION_ID;
if (!pipeArg || !sessionId) {
  console.error("ilmatto-agent-tools MCP requires --pipe <manager-pipe> and --session-id <session-id>");
  process.exit(2);
}

class ManagerRpcClient {
  private readonly socket: net.Socket;
  private buffer = "";
  private pending = new Map<string, { resolve: (value: any) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }>();

  constructor(pipeName: string) {
    const pipePath = pipeName.startsWith("\\\\.\\pipe\\") ? pipeName : `\\\\.\\pipe\\${pipeName}`;
    this.socket = net.createConnection(pipePath);
    this.socket.setEncoding("utf8");
    this.socket.on("data", (chunk: string) => this.onData(chunk));
    this.socket.on("error", (error) => this.failAll(error));
    this.socket.on("close", () => this.failAll(new Error("ManagerHost 连接已关闭。")));
  }

  async ready(): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("连接 ManagerHost 超时。")), 8_000);
      if (this.socket.readyState === "open") { clearTimeout(timer); resolve(); return; }
      this.socket.once("connect", () => { clearTimeout(timer); resolve(); });
      this.socket.once("error", (error) => { clearTimeout(timer); reject(error); });
    });
  }

  request(operation: CodexObservationOperation, args: Record<string, unknown>): Promise<any> {
    const requestId = `mcp-${randomUUID()}`;
    const message: ManagerClientMessage = {
      type: "codex_observation_request",
      sessionId: sessionId!,
      requestId,
      operation,
      workspacePath: typeof args.workspacePath === "string" ? args.workspacePath : undefined,
      prompt: typeof args.prompt === "string" ? args.prompt : undefined,
      attachments: Array.isArray(args.attachments) ? args.attachments as any : undefined,
      taskId: typeof args.taskId === "string" ? args.taskId : undefined,
      maxBytes: typeof args.maxBytes === "number" ? args.maxBytes : undefined,
    };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(requestId); reject(new Error("ManagerHost 请求超时。")); }, 30_000);
      this.pending.set(requestId, { resolve, reject, timer });
      this.socket.write(`${JSON.stringify(message)}\n`);
    });
  }

  requestAgentTool(operation: AgentToolRequest["operation"], args: Record<string, unknown>): Promise<any> {
    const requestId = `mcp-${randomUUID()}`;
    const message: AgentToolRequest = {
      type: "agent_tool_request",
      sessionId: sessionId!,
      requestId,
      operation,
      attachmentId: typeof args.attachment_id === "string" ? args.attachment_id : undefined,
      question: typeof args.question === "string" ? args.question : undefined,
    };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(requestId); reject(new Error("ManagerHost 请求超时。")); }, 30_000);
      this.pending.set(requestId, { resolve, reject, timer });
      this.socket.write(`${JSON.stringify(message)}\n`);
    });
  }

  requestCompanionMemory(operation: CompanionMemoryOperation, args: Record<string, unknown>): Promise<any> {
    const requestId = `mcp-${randomUUID()}`;
    const message: CompanionMemoryRequest = {
      type: "companion_memory_request",
      sessionId: sessionId!,
      requestId,
      operation,
      query: typeof args.query === "string" ? args.query : undefined,
      targetSessionId: typeof args.session_id === "string" ? args.session_id : undefined,
      limit: typeof args.limit === "number" ? args.limit : undefined,
      patch: operation === "session_update" && isRecord(args.patch) ? args.patch as any : undefined,
      profilePatch: operation === "profile_update" && isRecord(args.patch) ? args.patch as any : undefined,
    };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(requestId); reject(new Error("ManagerHost 请求超时。")); }, 30_000);
      this.pending.set(requestId, { resolve, reject, timer });
      this.socket.write(`${JSON.stringify(message)}\n`);
    });
  }

  close(): void { try { this.socket.end(); } catch { /* ignore */ } }

  private onData(chunk: string): void {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      newline = this.buffer.indexOf("\n");
      if (!line) continue;
      let message: any;
      try { message = JSON.parse(line); } catch { continue; }
      if ((message.type !== "codex_observation_response" && message.type !== "agent_tool_response" && message.type !== "companion_memory_response") || typeof message.requestId !== "string") continue;
      const pending = this.pending.get(message.requestId);
      if (!pending) continue;
      clearTimeout(pending.timer); this.pending.delete(message.requestId);
      if (message.ok === true) pending.resolve(message.data);
      else pending.reject(Object.assign(new Error(message.error?.message ?? "IlMatto Agent Tools 请求失败。"), { code: message.error?.code ?? "AGENT_TOOL_ERROR" }));
    }
  }

  private failAll(error: Error): void {
    for (const pending of this.pending.values()) { clearTimeout(pending.timer); pending.reject(error); }
    this.pending.clear();
  }
}

const client = new ManagerRpcClient(pipeArg!);
void client.ready().catch((error) => { console.error(error instanceof Error ? error.message : error); process.exitCode = 1; });

let inputBuffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  inputBuffer += chunk;
  let newline = inputBuffer.indexOf("\n");
  while (newline >= 0) {
    const line = inputBuffer.slice(0, newline).trim();
    inputBuffer = inputBuffer.slice(newline + 1);
    newline = inputBuffer.indexOf("\n");
    if (line) void handleMessage(line);
  }
});
process.stdin.on("end", () => client.close());

async function handleMessage(line: string): Promise<void> {
  let request: JsonRpcMessage;
  try { request = JSON.parse(line); } catch { return; }
  if (request.method === "notifications/initialized" || request.method === "notifications/cancelled") return;
  if (request.id === undefined || request.id === null) return;
  try {
    if (request.method === "initialize") {
      send({ jsonrpc: "2.0", id: request.id, result: {
        protocolVersion: typeof request.params?.protocolVersion === "string" ? request.params.protocolVersion : "2024-11-05",
        capabilities: { tools: { listChanged: false }, resources: { subscribe: false, listChanged: false } },
        serverInfo: { name: "ilmatto-agent-tools", version: "0.2.0" },
      } });
      return;
    }
    if (request.method === "ping") { send({ jsonrpc: "2.0", id: request.id, result: {} }); return; }
    if (request.method === "tools/list") { send({ jsonrpc: "2.0", id: request.id, result: { tools: toolDefinitions() } }); return; }
    if (request.method === "resources/list") { send({ jsonrpc: "2.0", id: request.id, result: { resources: resourceDefinitions() } }); return; }
    if (request.method === "resources/read") {
      const data = await readResource(String(request.params?.uri ?? ""));
      send({ jsonrpc: "2.0", id: request.id, result: { contents: [{ uri: String(request.params?.uri ?? ""), mimeType: "application/json", text: JSON.stringify(data) }] } });
      return;
    }
    if (request.method === "tools/call") {
      const result = await callTool(String(request.params?.name ?? ""), request.params?.arguments ?? {});
      send({ jsonrpc: "2.0", id: request.id, result: { content: [{ type: "text", text: JSON.stringify(result) }], structuredContent: result } });
      return;
    }
    send({ jsonrpc: "2.0", id: request.id, error: { code: -32601, message: `Unsupported MCP method: ${request.method}` } });
  } catch (error) {
    send({ jsonrpc: "2.0", id: request.id, result: { isError: true, content: [{ type: "text", text: JSON.stringify({ code: errorCode(error), message: error instanceof Error ? error.message : "Codex 观察请求失败。" }) }] } });
  }
}

async function callTool(name: string, args: Record<string, unknown>): Promise<unknown> {
  const requestedSession = typeof args.sessionId === "string" ? args.sessionId : sessionId;
  if (requestedSession !== sessionId) throw new Error("sessionId 与当前 Antigravity 会话不匹配。");
  const operation = toolOperation(name);
  if (operation) return client.request(operation, args);
  if (name === "identify_image") return client.requestAgentTool("identify_image", args);
  const memoryOperation = companionMemoryOperation(name);
  if (memoryOperation) return client.requestCompanionMemory(memoryOperation, args);
  throw new Error(`不支持的 IlMatto Agent Tools 工具：${name}`);
}

async function readResource(uri: string): Promise<unknown> {
  const report = /^codex:\/\/tasks\/([^/]+)\/(report|diff)$/.exec(uri);
  if (!report) throw new Error("不支持的 Codex 资源 URI。");
  const taskId = decodeURIComponent(report[1]);
  return client.request(report[2] === "report" ? "get_codex_report" : "get_codex_diff", { taskId });
}

function toolOperation(name: string): CodexObservationOperation | undefined {
  switch (name) {
    case "draft_codex_task": return "draft_codex_task";
    case "get_codex_status": return "get_codex_status";
    case "get_latest_codex_report": return "get_latest_codex_report";
    case "get_codex_report": return "get_codex_report";
    case "get_codex_diff": return "get_codex_diff";
    default: return undefined;
  }
}

function companionMemoryOperation(name: string): CompanionMemoryOperation | undefined {
  switch (name) {
    case "session_search": return "session_search";
    case "session_open": return "session_open";
    case "session_update": return "session_update";
    case "profile_update": return "profile_update";
    default: return undefined;
  }
}

function toolDefinitions(): unknown[] {
  const sessionProperty = { type: "string", description: "当前 Manager 会话标识；通常由 Facade 固定。" };
  return [
    { name: "draft_codex_task", description: "生成待用户检查并发送的 Codex Prompt 草稿；桌面端会将它载入为可编辑的 @codex 消息，不会启动或控制 Codex。", inputSchema: { type: "object", additionalProperties: false, required: ["prompt"], properties: { sessionId: sessionProperty, workspacePath: { type: "string" }, prompt: { type: "string" }, attachments: { type: "array" } } } },
    { name: "get_codex_status", description: "读取当前 Codex 的粗粒度状态。", inputSchema: { type: "object", additionalProperties: false, properties: { sessionId: sessionProperty } } },
    { name: "get_latest_codex_report", description: "按需读取当前会话最近一次 Codex 任务报告。", inputSchema: { type: "object", additionalProperties: false, properties: { sessionId: sessionProperty } } },
    { name: "get_codex_report", description: "读取指定 Codex 任务的只读报告。", inputSchema: { type: "object", additionalProperties: false, required: ["taskId"], properties: { sessionId: sessionProperty, taskId: { type: "string" } } } },
    { name: "get_codex_diff", description: "读取指定 Codex 任务保存的受限 diff。", inputSchema: { type: "object", additionalProperties: false, required: ["taskId"], properties: { sessionId: sessionProperty, taskId: { type: "string" }, maxBytes: { type: "integer", minimum: 1, maximum: 2000000 } } } },
    { name: "identify_image", description: "使用当前回合的受管图片附件进行只读网页识图。只能传当前附件的 attachment_id；返回的候选实体和相关性分数是辅助证据，不代表确定结论。", inputSchema: { type: "object", additionalProperties: false, required: ["attachment_id"], properties: { sessionId: sessionProperty, attachment_id: { type: "string", minLength: 1 }, question: { type: "string" } } } },
    { name: "session_search", description: "搜索 IlMatto 的跨会话摘要。只返回摘要和元数据，不返回原始会话；仅当用户提到过去的会话或你不确定历史细节时调用。", inputSchema: { type: "object", additionalProperties: false, required: ["query"], properties: { query: { type: "string", minLength: 1 }, limit: { type: "integer", minimum: 1, maximum: 5 } } } },
    { name: "session_open", description: "打开指定历史会话中与查询相关的少量可见消息片段。必须先用 session_search 找到该 session_id；不会返回完整会话、思路或工具输出。", inputSchema: { type: "object", additionalProperties: false, required: ["session_id", "query"], properties: { session_id: { type: "string" }, query: { type: "string", minLength: 1 } } } },
    { name: "session_update", description: "为当前会话追加事实性摘要、重要事件、未完成事项或关键词。没有跨会话价值时不要调用；每轮最多调用一次；不要写入内部思路、工具输出或未经支持的推断。", inputSchema: { type: "object", additionalProperties: false, required: ["patch"], properties: { patch: { type: "object", additionalProperties: false, properties: { title: { type: "string" }, summaryPatch: { type: "string" }, keyEvents: { type: "array", items: { type: "string" } }, openLoops: { type: "array", items: { type: "string" } }, keywords: { type: "array", items: { type: "string" } } } } } } },
    { name: "profile_update", description: "更新全局用户画像。只保存明确事实、稳定偏好、互动边界或用户明确要求记住的内容；不要保存一次性情绪或未经支持的性格推断。", inputSchema: { type: "object", additionalProperties: false, required: ["patch"], properties: { patch: { type: "object", additionalProperties: false, required: ["section"], properties: { section: { type: "string", enum: ["basic", "interests", "preferences", "boundaries", "current_topics"] }, add: { type: "array", items: { type: "string" } }, remove: { type: "array", items: { type: "string" } } } } } } },
  ];
}

function resourceDefinitions(): unknown[] {
  return [
    { uri: "codex://tasks/{taskId}/report", name: "Codex task report", mimeType: "application/json" },
    { uri: "codex://tasks/{taskId}/diff", name: "Codex task diff", mimeType: "text/plain" },
  ];
}

function send(message: JsonRpcMessage): void { process.stdout.write(`${JSON.stringify(message)}\n`); }

function argument(name: string): string | undefined {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : process.argv.find((value) => value.startsWith(`${name}=`))?.slice(name.length + 1);
}

function errorCode(error: unknown): string { return typeof (error as any)?.code === "string" ? (error as any).code : "MCP_ERROR"; }

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}
