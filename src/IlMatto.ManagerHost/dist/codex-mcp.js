import net from "node:net";
import process from "node:process";
import { randomUUID } from "node:crypto";
const pipeArg = argument("--pipe") ?? process.env.ILMATTO_MANAGER_PIPE;
const sessionId = argument("--session-id") ?? process.env.ILMATTO_MANAGER_SESSION_ID;
if (!pipeArg || !sessionId) {
    console.error("codex-mcp requires --pipe <manager-pipe> and --session-id <session-id>");
    process.exit(2);
}
class ManagerRpcClient {
    socket;
    buffer = "";
    pending = new Map();
    constructor(pipeName) {
        const pipePath = pipeName.startsWith("\\\\.\\pipe\\") ? pipeName : `\\\\.\\pipe\\${pipeName}`;
        this.socket = net.createConnection(pipePath);
        this.socket.setEncoding("utf8");
        this.socket.on("data", (chunk) => this.onData(chunk));
        this.socket.on("error", (error) => this.failAll(error));
        this.socket.on("close", () => this.failAll(new Error("ManagerHost 连接已关闭。")));
    }
    async ready() {
        await new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error("连接 ManagerHost 超时。")), 8_000);
            if (this.socket.readyState === "open") {
                clearTimeout(timer);
                resolve();
                return;
            }
            this.socket.once("connect", () => { clearTimeout(timer); resolve(); });
            this.socket.once("error", (error) => { clearTimeout(timer); reject(error); });
        });
    }
    request(operation, args) {
        const requestId = `mcp-${randomUUID()}`;
        const message = {
            type: "codex_observation_request",
            sessionId: sessionId,
            requestId,
            operation,
            workspacePath: typeof args.workspacePath === "string" ? args.workspacePath : undefined,
            prompt: typeof args.prompt === "string" ? args.prompt : undefined,
            attachments: Array.isArray(args.attachments) ? args.attachments : undefined,
            taskId: typeof args.taskId === "string" ? args.taskId : undefined,
            maxBytes: typeof args.maxBytes === "number" ? args.maxBytes : undefined,
        };
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { this.pending.delete(requestId); reject(new Error("ManagerHost 请求超时。")); }, 30_000);
            this.pending.set(requestId, { resolve, reject, timer });
            this.socket.write(`${JSON.stringify(message)}\n`);
        });
    }
    close() { try {
        this.socket.end();
    }
    catch { /* ignore */ } }
    onData(chunk) {
        this.buffer += chunk;
        let newline = this.buffer.indexOf("\n");
        while (newline >= 0) {
            const line = this.buffer.slice(0, newline).trim();
            this.buffer = this.buffer.slice(newline + 1);
            newline = this.buffer.indexOf("\n");
            if (!line)
                continue;
            let message;
            try {
                message = JSON.parse(line);
            }
            catch {
                continue;
            }
            if (message.type !== "codex_observation_response" || typeof message.requestId !== "string")
                continue;
            const pending = this.pending.get(message.requestId);
            if (!pending)
                continue;
            clearTimeout(pending.timer);
            this.pending.delete(message.requestId);
            if (message.ok === true)
                pending.resolve(message.data);
            else
                pending.reject(Object.assign(new Error(message.error?.message ?? "Codex 观察请求失败。"), { code: message.error?.code ?? "CODEX_OBSERVATION_ERROR" }));
        }
    }
    failAll(error) {
        for (const pending of this.pending.values()) {
            clearTimeout(pending.timer);
            pending.reject(error);
        }
        this.pending.clear();
    }
}
const client = new ManagerRpcClient(pipeArg);
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
        if (line)
            void handleMessage(line);
    }
});
process.stdin.on("end", () => client.close());
async function handleMessage(line) {
    let request;
    try {
        request = JSON.parse(line);
    }
    catch {
        return;
    }
    if (request.method === "notifications/initialized" || request.method === "notifications/cancelled")
        return;
    if (request.id === undefined || request.id === null)
        return;
    try {
        if (request.method === "initialize") {
            send({ jsonrpc: "2.0", id: request.id, result: {
                    protocolVersion: typeof request.params?.protocolVersion === "string" ? request.params.protocolVersion : "2024-11-05",
                    capabilities: { tools: { listChanged: false }, resources: { subscribe: false, listChanged: false } },
                    serverInfo: { name: "ilmatto-codex-observation", version: "0.1.0" },
                } });
            return;
        }
        if (request.method === "ping") {
            send({ jsonrpc: "2.0", id: request.id, result: {} });
            return;
        }
        if (request.method === "tools/list") {
            send({ jsonrpc: "2.0", id: request.id, result: { tools: toolDefinitions() } });
            return;
        }
        if (request.method === "resources/list") {
            send({ jsonrpc: "2.0", id: request.id, result: { resources: resourceDefinitions() } });
            return;
        }
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
    }
    catch (error) {
        send({ jsonrpc: "2.0", id: request.id, result: { isError: true, content: [{ type: "text", text: JSON.stringify({ code: errorCode(error), message: error instanceof Error ? error.message : "Codex 观察请求失败。" }) }] } });
    }
}
async function callTool(name, args) {
    const requestedSession = typeof args.sessionId === "string" ? args.sessionId : sessionId;
    if (requestedSession !== sessionId)
        throw new Error("sessionId 与当前 Antigravity 会话不匹配。");
    const operation = toolOperation(name);
    if (!operation)
        throw new Error(`不支持的 Codex 观察工具：${name}`);
    return client.request(operation, args);
}
async function readResource(uri) {
    const report = /^codex:\/\/tasks\/([^/]+)\/(report|diff)$/.exec(uri);
    if (!report)
        throw new Error("不支持的 Codex 资源 URI。");
    const taskId = decodeURIComponent(report[1]);
    return client.request(report[2] === "report" ? "get_codex_report" : "get_codex_diff", { taskId });
}
function toolOperation(name) {
    switch (name) {
        case "draft_codex_task": return "draft_codex_task";
        case "get_codex_status": return "get_codex_status";
        case "get_latest_codex_report": return "get_latest_codex_report";
        case "get_codex_report": return "get_codex_report";
        case "get_codex_diff": return "get_codex_diff";
        default: return undefined;
    }
}
function toolDefinitions() {
    const sessionProperty = { type: "string", description: "当前 Manager 会话标识；通常由 Facade 固定。" };
    return [
        { name: "draft_codex_task", description: "生成待用户检查并发送的 Codex Prompt 草稿；桌面端会将它载入为可编辑的 @codex 消息，不会启动或控制 Codex。", inputSchema: { type: "object", additionalProperties: false, required: ["prompt"], properties: { sessionId: sessionProperty, workspacePath: { type: "string" }, prompt: { type: "string" }, attachments: { type: "array" } } } },
        { name: "get_codex_status", description: "读取当前 Codex 的粗粒度状态。", inputSchema: { type: "object", additionalProperties: false, properties: { sessionId: sessionProperty } } },
        { name: "get_latest_codex_report", description: "按需读取当前会话最近一次 Codex 任务报告。", inputSchema: { type: "object", additionalProperties: false, properties: { sessionId: sessionProperty } } },
        { name: "get_codex_report", description: "读取指定 Codex 任务的只读报告。", inputSchema: { type: "object", additionalProperties: false, required: ["taskId"], properties: { sessionId: sessionProperty, taskId: { type: "string" } } } },
        { name: "get_codex_diff", description: "读取指定 Codex 任务保存的受限 diff。", inputSchema: { type: "object", additionalProperties: false, required: ["taskId"], properties: { sessionId: sessionProperty, taskId: { type: "string" }, maxBytes: { type: "integer", minimum: 1, maximum: 2000000 } } } },
    ];
}
function resourceDefinitions() {
    return [
        { uri: "codex://tasks/{taskId}/report", name: "Codex task report", mimeType: "application/json" },
        { uri: "codex://tasks/{taskId}/diff", name: "Codex task diff", mimeType: "text/plain" },
    ];
}
function send(message) { process.stdout.write(`${JSON.stringify(message)}\n`); }
function argument(name) {
    const index = process.argv.indexOf(name);
    return index >= 0 ? process.argv[index + 1] : process.argv.find((value) => value.startsWith(`${name}=`))?.slice(name.length + 1);
}
function errorCode(error) { return typeof error?.code === "string" ? error.code : "MCP_ERROR"; }
//# sourceMappingURL=codex-mcp.js.map