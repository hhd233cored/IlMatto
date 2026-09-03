import net from "node:net";
import path from "node:path";
import process from "node:process";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
export class PiWorkerBridge {
    settings;
    onEvent;
    provider = "pi";
    child;
    socket;
    buffer = "";
    started = false;
    constructor(settings, onEvent) {
        this.settings = settings;
        this.onEvent = onEvent;
    }
    get sessionRef() { return this.settings.sessionFile; }
    async start() {
        if (this.started)
            return;
        const pipeName = `IlMatto-Worker-${process.pid}-${crypto.randomUUID().replaceAll("-", "")}`;
        const script = findAgentHostScript();
        this.child = spawn(process.execPath, [script, "--pipe", pipeName], { cwd: path.dirname(script), windowsHide: true, stdio: ["ignore", "ignore", "pipe"] });
        let stderr = "";
        this.child.stderr?.setEncoding("utf8");
        this.child.stderr?.on("data", (chunk) => { stderr = `${stderr}${chunk}`.slice(-32_000); });
        this.child.on("exit", (code) => {
            this.started = false;
            if (code && code !== 0)
                this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "WORKER_EXITED", message: stderr || `Pi Worker exited with code ${code}` });
        });
        this.socket = await connectNamedPipe(pipeName, 8_000);
        this.socket.setEncoding("utf8");
        this.socket.on("data", (chunk) => this.onData(chunk));
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
    sendCodeTask(taskId, userRequest, _attachments) { this.send({ type: "code_task", sessionId: this.settings.sessionId, taskId, userRequest }); }
    approve(callId, approved) { this.send({ type: "approve_tool_call", sessionId: this.settings.sessionId, callId, approved }); }
    resolve(callId, approved) { this.approve(callId, approved); }
    cancel() { if (this.started)
        this.send({ type: "cancel", sessionId: this.settings.sessionId }); }
    deleteSession(sessionFile) { this.send({ type: "delete_session", sessionId: this.settings.sessionId, sessionFile }); }
    async dispose() {
        if (this.started) {
            try {
                this.send({ type: "shutdown", sessionId: this.settings.sessionId });
            }
            catch { }
        }
        this.socket?.end();
        this.socket?.destroy();
        this.started = false;
        const child = this.child;
        if (child && !child.killed) {
            await new Promise((resolve) => {
                const timer = setTimeout(() => { try {
                    child.kill();
                }
                catch { } resolve(); }, 2_000);
                child.once("exit", () => { clearTimeout(timer); resolve(); });
            });
        }
    }
    send(message) {
        if (!this.socket?.writable)
            throw new Error("Pi Worker is not connected");
        this.socket.write(`${JSON.stringify(message)}\n`);
    }
    onData(chunk) {
        this.buffer += chunk;
        let newline = this.buffer.indexOf("\n");
        while (newline >= 0) {
            const line = this.buffer.slice(0, newline).trim();
            this.buffer = this.buffer.slice(newline + 1);
            newline = this.buffer.indexOf("\n");
            if (!line)
                continue;
            try {
                this.onEvent(JSON.parse(line));
            }
            catch {
                this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "WORKER_PROTOCOL_ERROR", message: "Pi Worker returned invalid NDJSON" });
            }
        }
    }
}
function findAgentHostScript() {
    const directory = path.dirname(fileURLToPath(import.meta.url));
    const candidates = [
        process.env.ILMATTO_AGENT_HOST_SCRIPT,
        path.resolve(directory, "..", "..", "IlMatto.AgentHost", "dist", "index.js"),
        path.resolve(directory, "..", "..", "AgentHost", "dist", "index.js"),
        path.resolve(directory, "..", "..", "..", "IlMatto.AgentHost", "dist", "index.js"),
    ].filter((item) => Boolean(item));
    const found = candidates.find(existsSync);
    if (!found)
        throw new Error("Cannot locate IlMatto.AgentHost/dist/index.js");
    return found;
}
async function connectNamedPipe(pipeName, timeoutMs) {
    const address = pipeName.startsWith("\\\\.\\pipe\\") ? pipeName : `\\\\.\\pipe\\${pipeName}`;
    const deadline = Date.now() + timeoutMs;
    while (true) {
        try {
            return await new Promise((resolve, reject) => {
                const socket = net.createConnection(address);
                socket.once("connect", () => resolve(socket));
                socket.once("error", reject);
            });
        }
        catch (error) {
            if (Date.now() >= deadline)
                throw error;
            await new Promise((resolve) => setTimeout(resolve, 80));
        }
    }
}
//# sourceMappingURL=pi-worker.js.map