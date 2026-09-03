import { spawn } from "node:child_process";
import { existsSync, statSync } from "node:fs";
import path from "node:path";
import { codeResultSchema, validateCodeResult } from "./protocol.js";
const NO_CODEX_TIMEOUT_MS = 0;
const workerInstruction = `You are the IlMatto coding specialist. Make all technical and implementation decisions yourself.
Inspect the repository before changing it, keep every action within the user's request, validate relevant changes, and use your inherited Codex configuration and project instructions.
The coordinator has supplied only the user's original request and cannot approve, rewrite, or evaluate technical decisions.
Your final answer must be exactly one JSON object satisfying the supplied CodeResult JSON schema. Do not repeat the object, wrap it in a prose explanation, or stream it as user-facing commentary. Put the concise user-facing outcome in summaryForUser, list every changed file with additions/deletions, and record validation commands. If work cannot continue, return blocked with questions and needsUserDecision=true.`;
export function buildCodexTurnInput(userRequest, imagePaths = []) {
    return [
        { type: "text", text: `${workerInstruction}\n\n<user_request>\n${userRequest}\n</user_request>` },
        ...imagePaths.map((imagePath) => ({ type: "localImage", path: imagePath })),
    ];
}
export class CodexAppServerBridge {
    settings;
    onEvent;
    provider = "codex";
    child;
    stdoutBuffer = "";
    stderr = "";
    nextId = 1;
    pending = new Map();
    serverRequests = new Map();
    items = new Map();
    // App Server may stream a structured output response as an ordinary
    // agentMessage. Keep each message intact until item/completed so CodeResult
    // JSON never leaks into the chat transcript (and can be parsed even when
    // several JSON objects are concatenated by the provider).
    messageBuffers = new Map();
    heldMessageIds = new Set();
    answerParts = new Map();
    threadId;
    turnId;
    taskId;
    disposed = false;
    started = false;
    approvalPolicy;
    sandboxMode;
    autoApproveRequests = false;
    turnAutoApproveRequests = false;
    observationMode;
    spawnProcess;
    constructor(settings, onEvent, spawnProcess = spawn) {
        this.settings = settings;
        this.onEvent = onEvent;
        this.threadId = settings.threadId;
        this.observationMode = settings.mode === "observation";
        this.spawnProcess = spawnProcess;
        this.approvalPolicy = normalizeApprovalPolicy(settings.approvalPolicy);
        this.sandboxMode = normalizeSandboxMode(settings.sandboxMode);
        this.autoApproveRequests = isAlwaysApprovalPolicy(settings.approvalPolicy);
    }
    get sessionRef() { return this.threadId; }
    /** Apply model/effort changes to subsequent turns without restarting the
     * App Server process. Codex accepts both values on each turn/start call. */
    setModel(model) { this.settings.model = model?.trim() || undefined; }
    setEffort(effort) { this.settings.effort = effort?.trim() || undefined; }
    /** Update the policy used for the next Codex request without restarting the
     * hidden thread or App Server process. A currently running turn keeps the
     * policy it was started with, as required by Codex. */
    setApprovalPolicy(policy) {
        this.approvalPolicy = normalizeApprovalPolicy(policy);
        this.autoApproveRequests = isAlwaysApprovalPolicy(policy);
    }
    /** Update the sandbox used by subsequent Codex turns without restarting the
     * hidden thread. A currently running turn keeps its original policy. */
    setSandboxMode(mode) {
        this.sandboxMode = normalizeSandboxMode(mode);
    }
    async start() {
        if (this.started)
            return;
        this.startProcess();
        await this.request("initialize", {
            clientInfo: { name: "ilmatto", title: "IlMatto", version: "0.2.0" },
            capabilities: { experimentalApi: true, requestAttestation: false },
        }, NO_CODEX_TIMEOUT_MS);
        this.notify("initialized", {});
        const account = await this.request("account/read", { refreshToken: false }, NO_CODEX_TIMEOUT_MS);
        if (account?.requiresOpenaiAuth === true && !account?.account)
            throw new CodexWorkerError("CODEX_AUTH_REQUIRED", "Codex CLI 尚未登录。请在设置中登录 Codex 后重试。");
        let policy;
        try {
            const requirements = await this.request("configRequirements/read", {}, NO_CODEX_TIMEOUT_MS);
            policy = requirements?.requirements ? JSON.stringify(requirements.requirements) : undefined;
        }
        catch {
            policy = undefined;
        }
        const policyLabel = [policy, `approvalPolicy=${this.approvalPolicy}`].filter(Boolean).join(" · ");
        this.onEvent({ type: "provider_status", sessionId: this.settings.sessionId, available: true, authenticated: true, provider: "codex", policy: policyLabel });
        if (this.threadId) {
            const resumeParams = {
                threadId: this.threadId,
                cwd: path.resolve(this.settings.workspacePath),
                approvalPolicy: this.approvalPolicy,
                sandbox: appServerSandboxMode(this.sandboxMode),
            };
            if (this.settings.model?.trim())
                resumeParams.model = this.settings.model.trim();
            try {
                await this.requestWithApprovalFallback("thread/resume", resumeParams, NO_CODEX_TIMEOUT_MS);
            }
            catch (error) {
                throw new CodexWorkerError("CODEX_THREAD_RESUME_FAILED", error instanceof Error ? error.message : "Codex thread 恢复失败。");
            }
        }
        else {
            const params = {
                cwd: path.resolve(this.settings.workspacePath),
                approvalPolicy: this.approvalPolicy,
                sandbox: appServerSandboxMode(this.sandboxMode),
                serviceName: "ilmatto",
            };
            if (this.settings.model?.trim())
                params.model = this.settings.model.trim();
            const started = await this.requestWithApprovalFallback("thread/start", params, NO_CODEX_TIMEOUT_MS);
            this.threadId = started?.thread?.id ?? started?.thread?.sessionId;
            if (!this.threadId)
                throw new CodexWorkerError("CODEX_PROTOCOL_ERROR", "Codex thread/start 没有返回 thread ID。");
        }
        this.started = true;
        this.onEvent({ type: "session_ready", sessionId: this.settings.sessionId, sessionRef: this.threadId, threadId: this.threadId, provider: "codex" });
    }
    sendCodeTask(taskId, userRequest, attachments = []) {
        if (!this.started || !this.threadId)
            throw new CodexWorkerError("CODEX_PROTOCOL_ERROR", "Codex App Server 尚未启动。");
        if (this.turnId)
            throw new CodexWorkerError("BUSY", "Codex Coding Agent 正在运行另一个任务。");
        this.taskId = taskId;
        // Capture the selected policies in the turn request. Changing either
        // selector while a turn is running must not silently alter that turn.
        this.items.clear();
        this.messageBuffers.clear();
        this.heldMessageIds.clear();
        this.answerParts.clear();
        const imagePaths = attachments.map((attachment) => validateImageAttachment(attachment));
        const params = {
            threadId: this.threadId,
            input: this.observationMode
                ? [{ type: "text", text: userRequest }, ...imagePaths.map((imagePath) => ({ type: "localImage", path: imagePath }))]
                : buildCodexTurnInput(userRequest, imagePaths),
            cwd: path.resolve(this.settings.workspacePath),
            approvalPolicy: this.approvalPolicy,
            sandboxPolicy: buildSandboxPolicy(this.sandboxMode, this.settings.workspacePath),
            summary: "concise",
        };
        this.turnAutoApproveRequests = this.autoApproveRequests;
        if (!this.observationMode)
            params.outputSchema = codeResultSchema;
        if (this.settings.model?.trim())
            params.model = this.settings.model.trim();
        if (this.settings.effort?.trim())
            params.effort = this.settings.effort.trim();
        void this.requestWithApprovalFallback("turn/start", params, NO_CODEX_TIMEOUT_MS).then((result) => {
            // A very fast App Server can emit turn/completed before the response to
            // turn/start resolves. Do not resurrect a completed turn in that case.
            if (this.taskId === taskId)
                this.turnId = result?.turn?.id ?? this.turnId;
        }).catch((error) => this.failTurn(error));
    }
    resolve(requestId, approved, values) {
        const request = this.serverRequests.get(requestId);
        if (!request)
            throw new CodexWorkerError("CODEX_PROTOCOL_ERROR", "Codex 交互请求已完成或不存在。");
        this.serverRequests.delete(requestId);
        this.write({ id: request.id, result: approvalResult(request.method, request.params, approved, values) });
    }
    cancel() {
        if (this.threadId && this.turnId && this.child?.stdin.writable) {
            void this.request("turn/interrupt", { threadId: this.threadId, turnId: this.turnId }, NO_CODEX_TIMEOUT_MS).catch(() => undefined);
        }
    }
    async deleteSession() {
        if (this.threadId && this.child?.stdin.writable)
            await this.request("thread/delete", { threadId: this.threadId }, NO_CODEX_TIMEOUT_MS);
    }
    async dispose() {
        if (this.disposed)
            return;
        this.disposed = true;
        this.cancel();
        for (const pending of this.pending.values()) {
            clearTimeout(pending.timer);
            pending.reject(new Error("Codex App Server 已关闭。"));
        }
        this.pending.clear();
        const child = this.child;
        this.child = undefined;
        if (child && !child.killed) {
            try {
                child.stdin.end();
            }
            catch { }
            await new Promise((resolve) => {
                const timer = setTimeout(() => { try {
                    child.kill();
                }
                catch { } resolve(); }, 2_000);
                child.once("exit", () => { clearTimeout(timer); resolve(); });
            });
        }
    }
    startProcess() {
        const executable = this.settings.executable?.trim() || "codex";
        try {
            const child = this.spawnProcess(executable, ["app-server"], {
                cwd: path.resolve(this.settings.workspacePath), windowsHide: true,
                stdio: ["pipe", "pipe", "pipe"], env: { ...process.env },
            });
            this.child = child;
            child.stdout.setEncoding("utf8");
            child.stderr.setEncoding("utf8");
            child.stdout.on("data", (chunk) => this.onStdout(chunk));
            child.stderr.on("data", (chunk) => { this.stderr = `${this.stderr}${chunk}`.slice(-32_000); });
            child.on("error", (error) => this.failProcess(new CodexWorkerError("CODEX_NOT_FOUND", error.message)));
            child.on("exit", (code) => {
                if (!this.disposed && code !== 0)
                    this.failProcess(new CodexWorkerError("CODEX_PROTOCOL_ERROR", redact(`${this.stderr || `Codex App Server exited with code ${code}`}`)));
            });
        }
        catch (error) {
            throw new CodexWorkerError("CODEX_NOT_FOUND", error instanceof Error ? error.message : "无法启动 Codex CLI。");
        }
    }
    request(method, params, timeoutMs) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = timeoutMs > 0 ? setTimeout(() => { this.pending.delete(id); reject(new CodexWorkerError("CODEX_PROTOCOL_ERROR", `Codex 请求超时：${method}`)); }, timeoutMs) : undefined;
            this.pending.set(id, { resolve, reject, timer });
            this.write({ method, id, params });
        });
    }
    async requestWithApprovalFallback(method, params, timeoutMs) {
        try {
            return await this.request(method, params, timeoutMs);
        }
        catch (error) {
            if (this.approvalPolicy !== "unlessTrusted" || !isLegacyApprovalPolicyError(error))
                throw error;
            this.approvalPolicy = "untrusted";
            return this.request(method, { ...params, approvalPolicy: this.approvalPolicy }, timeoutMs);
        }
    }
    notify(method, params) { this.write({ method, params }); }
    write(message) {
        if (!this.child?.stdin.writable)
            throw new CodexWorkerError("CODEX_NOT_FOUND", "Codex App Server stdin 不可用。");
        this.child.stdin.write(`${JSON.stringify(message)}\n`);
    }
    onStdout(chunk) {
        this.stdoutBuffer += chunk;
        let newline = this.stdoutBuffer.indexOf("\n");
        while (newline >= 0) {
            const line = this.stdoutBuffer.slice(0, newline).trim();
            this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1);
            newline = this.stdoutBuffer.indexOf("\n");
            if (!line)
                continue;
            let message;
            try {
                message = JSON.parse(line);
            }
            catch {
                continue;
            }
            if (message.id !== undefined && message.method === undefined) {
                const pending = this.pending.get(message.id);
                if (!pending)
                    continue;
                clearTimeout(pending.timer);
                this.pending.delete(message.id);
                if (message.error)
                    pending.reject(new CodexWorkerError("CODEX_PROTOCOL_ERROR", redact(message.error?.message ?? JSON.stringify(message.error))));
                else
                    pending.resolve(message.result);
                continue;
            }
            if (message.id !== undefined && typeof message.method === "string") {
                this.onServerRequest(message);
                continue;
            }
            if (typeof message.method === "string")
                this.onNotification(message.method, message.params ?? {});
        }
    }
    onServerRequest(message) {
        const method = String(message.method);
        const requestId = `codex-${message.id}`;
        const params = message.params ?? {};
        this.serverRequests.set(requestId, { id: message.id, method, params });
        // File-change approvals are often sent with only an itemId and no diff or
        // reason. The item/started notification already contains the concrete
        // paths (and, for some Codex versions, the patch), so use it to enrich the
        // request before it reaches the desktop approval card.
        const interaction = describeInteraction(method, params, this.findRelatedItem(method, params)) ?? describeGenericApproval(method, params);
        if (!interaction) {
            this.serverRequests.delete(requestId);
            this.write({ id: message.id, error: { code: -32601, message: `IlMatto does not support Codex request ${method}` } });
            return;
        }
        // `always` applies to Codex approval prompts, not to MCP elicitation
        // forms or user-input questions that may contain arbitrary data.
        if (this.turnAutoApproveRequests && isAutoApprovalRequestMethod(method)) {
            this.serverRequests.delete(requestId);
            this.write({ id: message.id, result: approvalResult(method, params, true) });
            this.onEvent({ type: "interaction_completed", sessionId: this.settings.sessionId, requestId, provider: "codex", autoApproved: true });
            return;
        }
        this.onEvent({ type: "interaction_request", sessionId: this.settings.sessionId, requestId, provider: "codex", ...interaction });
    }
    findRelatedItem(method, params) {
        const explicitId = params?.itemId ?? params?.item?.id ?? params?.item?.itemId;
        if (explicitId !== undefined) {
            const item = this.items.get(String(explicitId));
            if (item)
                return item;
        }
        const expectedType = method.includes("fileChange") ? "fileChange" : method.includes("commandExecution") ? "commandExecution" : undefined;
        if (!expectedType)
            return undefined;
        // Map preserves insertion order. The most recently started matching item
        // is the best fallback for App Server builds that omit itemId.
        return [...this.items.values()].reverse().find((item) => item?.type === expectedType);
    }
    onNotification(method, params) {
        if (method === "thread/started" && params?.thread?.id)
            this.threadId = params.thread.id;
        if (method === "turn/started")
            this.turnId = params?.turn?.id ?? params?.turnId ?? this.turnId;
        if (method === "item/started")
            this.onItemStarted(params?.item ?? params);
        if (method === "item/completed")
            this.onItemCompleted(params?.item ?? params);
        if (method === "item/agentMessage/delta")
            this.onAgentDelta(params);
        if (method === "item/reasoning/summaryTextDelta")
            this.onEvent({ type: "thinking_delta", sessionId: this.settings.sessionId, taskId: this.taskId, text: params?.delta ?? params?.text ?? "" });
        if (method === "item/reasoning/textDelta")
            this.onEvent({ type: "thinking_delta", sessionId: this.settings.sessionId, taskId: this.taskId, text: params?.delta ?? params?.text ?? "" });
        if (method === "item/commandExecution/outputDelta")
            this.onEvent({ type: "tool_output", sessionId: this.settings.sessionId, callId: params?.itemId ?? "command", tool: "run_command", text: params?.delta ?? "" });
        if (method === "item/fileChange/outputDelta")
            this.onEvent({ type: "tool_output", sessionId: this.settings.sessionId, callId: params?.itemId ?? "file-change", tool: "modify_files", text: params?.delta ?? "" });
        if (method === "item/fileChange/patchUpdated")
            this.onEvent({ type: "tool_output", sessionId: this.settings.sessionId, callId: params?.itemId ?? "file-change", tool: "modify_files", text: patchUpdateText(params?.changes) });
        if (method === "serverRequest/resolved") {
            const requestId = `codex-${params?.requestId}`;
            this.serverRequests.delete(requestId);
            this.onEvent({ type: "interaction_completed", sessionId: this.settings.sessionId, requestId, provider: "codex" });
        }
        if (method === "turn/diff/updated")
            this.onEvent({ type: "tool_output", sessionId: this.settings.sessionId, callId: params?.turnId ?? "diff", tool: "modify_files", text: params?.diff ?? "" });
        if (method === "turn/completed")
            this.completeTurn(params);
        if (method === "error")
            this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "CODEX_PROTOCOL_ERROR", message: redact(params?.error?.message ?? params?.message ?? "Codex turn failed") });
    }
    onAgentDelta(params) {
        const itemId = String(params?.itemId ?? "");
        const item = this.items.get(itemId);
        const delta = String(params?.delta ?? "");
        if (!delta)
            return;
        const combined = `${this.messageBuffers.get(itemId) ?? ""}${delta}`;
        this.messageBuffers.set(itemId, combined);
        if (this.observationMode) {
            this.onEvent({ type: "assistant_delta", sessionId: this.settings.sessionId, taskId: this.taskId, text: delta });
            return;
        }
        const phase = item?.phase ?? params?.phase;
        if (phase === "commentary") {
            // Structured output normally starts with `{` (or a fenced JSON block).
            // Hold such a message until completion; this also handles the first
            // delta being only a single opening brace.
            if (this.heldMessageIds.has(itemId) || looksLikeStructuredResultPrefix(combined)) {
                this.heldMessageIds.add(itemId);
                return;
            }
            this.onEvent({ type: "assistant_delta", sessionId: this.settings.sessionId, taskId: this.taskId, text: delta });
        }
        else {
            this.answerParts.set(itemId, combined);
        }
    }
    onItemStarted(item) {
        if (!item?.id)
            return;
        this.items.set(String(item.id), item);
        const details = itemDetails(item);
        if (details)
            this.onEvent({ type: "tool_started", sessionId: this.settings.sessionId, callId: String(item.id), tool: details.tool, command: details.command });
    }
    onItemCompleted(item) {
        if (!item?.id)
            return;
        const itemId = String(item.id);
        this.items.set(itemId, item);
        if (item.type === "agentMessage") {
            const buffered = this.messageBuffers.get(itemId) ?? "";
            const fullText = typeof item.text === "string" && item.text.length > 0 ? item.text : buffered;
            if (this.observationMode) {
                if (fullText)
                    this.answerParts.set(itemId, fullText);
                this.messageBuffers.delete(itemId);
                this.heldMessageIds.delete(itemId);
                return;
            }
            const structured = parseCodeResult(fullText);
            if (structured) {
                // A commentary item containing CodeResult is a protocol payload, not
                // user-facing prose. Store it for turn/completed and keep it out of
                // assistant_delta entirely.
                if (fullText)
                    this.answerParts.set(itemId, fullText);
            }
            else if (item.phase !== "commentary") {
                if (fullText)
                    this.answerParts.set(itemId, fullText);
            }
            else if (this.heldMessageIds.has(itemId) && fullText) {
                // It looked like JSON while streaming but was ordinary text after all.
                // Flush it once, avoiding duplicate deltas.
                this.onEvent({ type: "assistant_delta", sessionId: this.settings.sessionId, taskId: this.taskId, text: fullText });
            }
            this.messageBuffers.delete(itemId);
            this.heldMessageIds.delete(itemId);
            return;
        }
        const details = itemDetails(item);
        if (!details)
            return;
        const ok = !["failed", "declined", "cancelled"].includes(String(item.status));
        this.onEvent({ type: "tool_completed", sessionId: this.settings.sessionId, callId: String(item.id), tool: details.tool, ok, summary: details.summary, output: item.aggregatedOutput ?? item.result, diff: details.diff });
    }
    completeTurn(params) {
        const taskId = this.taskId;
        this.turnId = undefined;
        this.taskId = undefined;
        this.turnAutoApproveRequests = false;
        if (!taskId)
            return;
        const status = params?.turn?.status ?? params?.status;
        if (status === "interrupted" || status === "cancelled") {
            if (this.observationMode)
                this.onEvent({ type: "observation_result", sessionId: this.settings.sessionId, taskId, status: "cancelled", text: "代码任务已取消。" });
            else
                this.onEvent({ type: "code_result", sessionId: this.settings.sessionId, taskId, result: cancelledResult() });
            return;
        }
        if (this.observationMode) {
            const streamedText = [...this.answerParts.values(), ...this.messageBuffers.values()].join("\n").trim();
            this.onEvent({
                type: "observation_result",
                sessionId: this.settings.sessionId,
                taskId,
                status: String(status ?? "completed").toLowerCase(),
                text: streamedText,
            });
            return;
        }
        const result = parseCodeResult(Array.from(this.answerParts.values()).join("\n"));
        if (!result) {
            this.onEvent({ type: "error", sessionId: this.settings.sessionId, code: "CODEX_RESULT_INVALID", message: "Codex 已结束，但没有返回符合 CodeResult Schema 的结果。" });
            this.onEvent({ type: "code_result", sessionId: this.settings.sessionId, taskId, result: failedResult("Codex 未返回有效的结构化完成结果。") });
            return;
        }
        this.onEvent({ type: "code_result", sessionId: this.settings.sessionId, taskId, result });
    }
    failTurn(error) {
        const taskId = this.taskId;
        this.taskId = undefined;
        this.turnId = undefined;
        this.turnAutoApproveRequests = false;
        const code = error instanceof CodexWorkerError ? error.code : "CODEX_PROTOCOL_ERROR";
        const message = redact(error instanceof Error ? error.message : "Codex Coding Agent 失败。");
        this.onEvent({ type: "error", sessionId: this.settings.sessionId, code, message });
        if (taskId) {
            if (this.observationMode)
                this.onEvent({ type: "observation_result", sessionId: this.settings.sessionId, taskId, status: "failed", text: message });
            else
                this.onEvent({ type: "code_result", sessionId: this.settings.sessionId, taskId, result: failedResult(message) });
        }
    }
    failProcess(error) {
        for (const pending of this.pending.values()) {
            clearTimeout(pending.timer);
            pending.reject(error);
        }
        this.pending.clear();
        if (this.taskId)
            this.failTurn(error);
    }
}
function validateImageAttachment(attachment) {
    const imagePath = path.resolve(attachment.path);
    if (!existsSync(imagePath))
        throw new CodexWorkerError("CODEX_IMAGE_NOT_FOUND", `找不到图片附件：${imagePath}`);
    if (!statSync(imagePath).isFile())
        throw new CodexWorkerError("CODEX_IMAGE_INVALID", `图片附件不是文件：${imagePath}`);
    if (!/\.(?:png|jpe?g|gif|webp|bmp|tiff?|svg)$/i.test(imagePath)) {
        throw new CodexWorkerError("CODEX_IMAGE_UNSUPPORTED", `Codex 暂不支持该图片格式：${imagePath}`);
    }
    return imagePath;
}
export class CodexWorkerError extends Error {
    code;
    constructor(code, message) {
        super(message);
        this.code = code;
    }
}
function normalizeApprovalPolicy(value) {
    switch (value?.trim().toLowerCase()) {
        // Migrate values written by older IlMatto builds to native policies.
        case "manual": return "on-request";
        // `always` is represented to Codex as its safest native policy and is
        // enforced by the Host's auto-approval branch above.
        case "always": return "untrusted";
        case "on-request": return "on-request";
        case "never": return "never";
        case "unlesstrusted": return "unlessTrusted";
        case "untrusted": return "untrusted";
        default: return "on-request";
    }
}
function isAlwaysApprovalPolicy(value) {
    return value?.trim().toLowerCase() === "always";
}
function normalizeSandboxMode(value) {
    switch (value?.trim().toLowerCase()) {
        case "read-only":
        case "readonly":
        case "read_only": return "read-only";
        case "danger-full-access":
        case "dangerfullaccess":
        case "danger_full_access": return "danger-full-access";
        case "workspace-write":
        case "workspacewrite":
        case "workspace_write":
        default: return "workspace-write";
    }
}
/**
 * `thread/start` and `thread/resume` use Codex's public sandbox enum names.
 * These are intentionally hyphenated; the camelCase discriminators belong to
 * the `sandboxPolicy` object used by `turn/start` (see buildSandboxPolicy).
 */
export function appServerSandboxMode(mode) {
    switch (mode) {
        case "read-only":
        case "workspace-write":
        case "danger-full-access":
            return mode;
        default:
            return "workspace-write";
    }
}
export function buildSandboxPolicy(mode, workspacePath) {
    switch (mode) {
        case "read-only": return { type: "readOnly" };
        case "danger-full-access": return { type: "dangerFullAccess" };
        case "workspace-write":
        default: return { type: "workspaceWrite", writableRoots: [path.resolve(workspacePath)], networkAccess: false };
    }
}
/** Detect the protocol error emitted by older Codex App Server builds when
 * they do not know the newer `unlessTrusted` spelling. */
export function isLegacyApprovalPolicyError(error) {
    // Rust's serde error uses backticks around enum variants (`unlessTrusted`);
    // normalize quote styles before matching so the fallback also works with
    // builds that use single/double quotes or no quoting at all.
    const text = (error instanceof Error ? error.message : String(error ?? "")).replace(/[\u0060'\"]/g, "");
    return /unknown\s+variant\s+unlessTrusted/i.test(text) && /expected\s+one\s+of[\s\S]*\buntrusted\b/i.test(text);
}
export async function probeCodexAppServer(executable, workspacePath) {
    const client = new CodexControlClient(executable, workspacePath);
    try {
        const initialized = await client.connect();
        const [account, models, requirements] = await Promise.all([
            client.request("account/read", { refreshToken: false }, NO_CODEX_TIMEOUT_MS),
            client.request("model/list", { limit: 100, includeHidden: false }, NO_CODEX_TIMEOUT_MS).catch(() => ({ data: [] })),
            client.request("configRequirements/read", {}, NO_CODEX_TIMEOUT_MS).catch(() => ({ requirements: null })),
        ]);
        return {
            available: true,
            authenticated: account?.requiresOpenaiAuth !== true || Boolean(account?.account),
            version: initialized?.userAgent,
            models: Array.isArray(models?.data) ? models.data.map((model) => ({
                id: String(model?.model ?? model?.id ?? ""), displayName: String(model?.displayName ?? model?.model ?? model?.id ?? ""),
                efforts: Array.isArray(model?.supportedReasoningEfforts) ? model.supportedReasoningEfforts.map((item) => String(item?.reasoningEffort ?? item)).filter(Boolean) : [],
            })).filter((model) => model.id) : [],
            policy: requirements?.requirements ? JSON.stringify(requirements.requirements) : undefined,
        };
    }
    finally {
        await client.dispose();
    }
}
export async function startCodexAppServerLogin(executable, workspacePath) {
    let completeLogin;
    const completion = new Promise((resolve) => { completeLogin = resolve; });
    const client = new CodexControlClient(executable, workspacePath, (method, params) => {
        if (method === "account/login/completed")
            completeLogin({ loginId: params?.loginId ?? undefined, ok: params?.success === true, message: params?.error ?? undefined });
    });
    try {
        await client.connect();
        const login = await client.request("account/login/start", { type: "chatgpt", codexStreamlinedLogin: true, useHostedLoginSuccessPage: true, appBrand: "codex" }, NO_CODEX_TIMEOUT_MS);
        if (login?.type !== "chatgpt" || !login?.loginId || !login?.authUrl)
            throw new CodexWorkerError("CODEX_PROTOCOL_ERROR", "Codex account/login/start 没有返回浏览器登录 URL。");
        return { loginId: String(login.loginId), authUrl: String(login.authUrl), completion, dispose: () => client.dispose() };
    }
    catch (error) {
        await client.dispose();
        throw error;
    }
}
class CodexControlClient {
    executable;
    cwd;
    notification;
    child;
    buffer = "";
    stderr = "";
    nextId = 1;
    pending = new Map();
    constructor(executable, cwd, notification) {
        this.executable = executable;
        this.cwd = cwd;
        this.notification = notification;
    }
    async connect() {
        const command = this.executable?.trim() || "codex";
        try {
            const child = spawn(command, ["app-server"], { cwd: path.resolve(this.cwd), windowsHide: true, stdio: ["pipe", "pipe", "pipe"], env: { ...process.env } });
            this.child = child;
            child.stdout.setEncoding("utf8");
            child.stderr.setEncoding("utf8");
            child.stdout.on("data", (chunk) => this.onStdout(chunk));
            child.stderr.on("data", (chunk) => { this.stderr = `${this.stderr}${chunk}`.slice(-16_000); });
            child.on("error", (error) => this.fail(new CodexWorkerError("CODEX_NOT_FOUND", error.message)));
            child.on("exit", (code) => { if (code !== 0)
                this.fail(new CodexWorkerError("CODEX_PROTOCOL_ERROR", redact(this.stderr || `Codex App Server exited with code ${code}`))); });
        }
        catch (error) {
            throw new CodexWorkerError("CODEX_NOT_FOUND", error instanceof Error ? error.message : "无法启动 Codex CLI。");
        }
        const initialized = await this.request("initialize", {
            clientInfo: { name: "ilmatto", title: "IlMatto", version: "0.2.0" },
            capabilities: { experimentalApi: true, requestAttestation: false },
        }, NO_CODEX_TIMEOUT_MS);
        this.write({ method: "initialized", params: {} });
        return initialized;
    }
    request(method, params, timeoutMs) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = timeoutMs > 0 ? setTimeout(() => { this.pending.delete(id); reject(new CodexWorkerError("CODEX_PROTOCOL_ERROR", `Codex 请求超时：${method}`)); }, timeoutMs) : undefined;
            this.pending.set(id, { resolve, reject, timer });
            this.write({ method, id, params });
        });
    }
    async dispose() {
        for (const pending of this.pending.values()) {
            clearTimeout(pending.timer);
            pending.reject(new Error("Codex App Server 已关闭。"));
        }
        this.pending.clear();
        const child = this.child;
        this.child = undefined;
        if (!child || child.killed)
            return;
        try {
            child.stdin.end();
        }
        catch { }
        await new Promise((resolve) => {
            const timer = setTimeout(() => { try {
                child.kill();
            }
            catch { } resolve(); }, 1_500);
            child.once("exit", () => { clearTimeout(timer); resolve(); });
        });
    }
    onStdout(chunk) {
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
            if (message.id !== undefined && message.method === undefined) {
                const pending = this.pending.get(Number(message.id));
                if (!pending)
                    continue;
                clearTimeout(pending.timer);
                this.pending.delete(Number(message.id));
                if (message.error)
                    pending.reject(new CodexWorkerError("CODEX_PROTOCOL_ERROR", redact(message.error?.message ?? JSON.stringify(message.error))));
                else
                    pending.resolve(message.result);
            }
            else if (typeof message.method === "string")
                this.notification?.(message.method, message.params ?? {});
        }
    }
    write(message) {
        if (!this.child?.stdin.writable)
            throw new CodexWorkerError("CODEX_NOT_FOUND", "Codex App Server stdin 不可用。");
        this.child.stdin.write(`${JSON.stringify(message)}\n`);
    }
    fail(error) {
        for (const pending of this.pending.values()) {
            clearTimeout(pending.timer);
            pending.reject(error);
        }
        this.pending.clear();
    }
}
export function describeInteraction(method, params, relatedItem) {
    if (method === "item/commandExecution/requestApproval") {
        const command = commandText(params?.command) || firstText(params?.commandLine, params?.cmd, params?.argv, params?.args, params?.item?.command, relatedItem?.command);
        const cwd = firstText(params?.cwd, params?.workingDirectory, params?.workdir, params?.item?.cwd, relatedItem?.cwd);
        const reason = firstText(params?.reason, params?.message, params?.prompt, params?.item?.reason, relatedItem?.reason);
        const details = [reason, command, cwd].filter(Boolean).join("\n") || "Codex 请求执行命令，但未提供命令详情。";
        return { kind: "command_approval", title: "Codex 请求执行命令", details };
    }
    if (method === "item/fileChange/requestApproval") {
        const reason = firstText(params?.reason, params?.message, params?.prompt, params?.item?.reason, relatedItem?.reason);
        const paths = filePaths(params, relatedItem);
        const diff = firstText(params?.diff, params?.patch, params?.item?.diff, relatedItem?.diff) || patchUpdateText(params?.changes ?? params?.patches ?? params?.item?.changes ?? relatedItem?.changes);
        const details = [reason, paths ? `文件：${paths}` : ""].filter(Boolean).join("\n") || "Codex 请求修改文件，但未提供文件详情。";
        return { kind: "file_approval", title: "Codex 请求修改文件", details, ...(diff ? { diff } : {}) };
    }
    if (method === "item/permissions/requestApproval") {
        const reason = firstText(params?.reason, params?.message, params?.prompt);
        const requested = params?.permissions ?? params?.requestedPermissions ?? params?.requestedScopes ?? params?.scope;
        const permissionText = requested === undefined ? "" : `申请权限：${jsonText(requested)}`;
        const details = [reason, permissionText].filter(Boolean).join("\n") || "Codex 请求额外权限，但未提供权限详情。";
        return { kind: "permissions", title: "Codex 请求额外权限", details, fields: requested };
    }
    if (method === "item/tool/requestUserInput") {
        const details = firstText(params?.message, params?.prompt, params?.question, params?.reason) || "请回答 Codex 的问题";
        return { kind: "question", title: "Codex 需要你的输入", details, fields: params?.questions ?? params?.requestedSchema ?? params?.schema };
    }
    if (method === "mcpServer/elicitation/request") {
        const request = params?.request ?? params;
        const serverName = firstText(params?.serverName, params?.server, request?.serverName);
        const titleSuffix = serverName ? ` ${serverName}` : "";
        const details = firstText(request?.message, request?.prompt, params?.message) || (request?.mode === "url" ? "请确认 URL 授权" : "请填写 MCP 表单");
        if (request?.mode === "url") {
            const url = firstText(request?.url, params?.url);
            return { kind: "mcp_url", title: `MCP${titleSuffix} 请求授权`, details, ...(url ? { url } : {}) };
        }
        return { kind: "mcp_form", title: `MCP${titleSuffix} 请求输入`, details, fields: request?.requestedSchema ?? request?.schema ?? request?.form };
    }
    return undefined;
}
/**
 * Keep approval-like requests human-reviewable even when a newer Codex
 * version introduces a method that this host does not know yet. We still
 * reject unrelated server requests, but never turn an unknown approval into
 * an automatic decline.
 */
function describeGenericApproval(method, params) {
    if (!isApprovalRequestMethod(method) && !/requestUserInput$/i.test(method))
        return undefined;
    const serialized = redact(jsonText(params ?? {})).slice(0, 12_000);
    const details = serialized && serialized !== "{}"
        ? `Codex 请求人工审批（${method}）：\n${serialized}`
        : `Codex 请求人工审批（${method}），但未提供结构化详情。`;
    return { kind: "question", title: "Codex 请求人工审批", details };
}
function isApprovalRequestMethod(method) {
    return /requestApproval$/i.test(method) || method === "mcpServer/elicitation/request";
}
function isAutoApprovalRequestMethod(method) {
    return /requestApproval$/i.test(method);
}
function approvalResult(method, params, approved, values) {
    if (method === "item/commandExecution/requestApproval" || method === "item/fileChange/requestApproval" || /requestApproval$/i.test(method)) {
        if (method === "item/permissions/requestApproval") {
            const requested = params?.permissions ?? params?.requestedPermissions ?? {};
            return {
                permissions: approved
                    ? { ...(requested.network ? { network: requested.network } : {}), ...(requested.fileSystem ? { fileSystem: requested.fileSystem } : {}) }
                    : {},
                scope: "turn",
            };
        }
        return { decision: approved ? "accept" : "decline" };
    }
    if (method === "item/tool/requestUserInput" || /requestUserInput$/i.test(method)) {
        return { answers: approved ? normalizeUserInputAnswers(params?.questions, values) : {} };
    }
    if (method === "mcpServer/elicitation/request" || /elicitation\/request$/i.test(method)) {
        return { action: approved ? "accept" : "decline", content: approved ? (values ?? {}) : null, _meta: null };
    }
    return { decision: approved ? "accept" : "decline" };
}
export function itemDetails(item) {
    switch (item?.type) {
        case "commandExecution": return { tool: "run_command", command: commandText(item.command), summary: item.status === "completed" ? `命令完成${item.exitCode !== undefined ? `，退出码 ${item.exitCode}` : ""}` : `命令${item.status ?? "结束"}` };
        case "fileChange": return { tool: "modify_files", command: (item.changes ?? []).map((change) => change.path).join("\n"), summary: `${(item.changes ?? []).length} 个文件变更`, diff: (item.changes ?? []).map((change) => change.diff ?? "").filter(Boolean).join("\n") };
        case "mcpToolCall": return { tool: `mcp:${item.server ?? "server"}/${item.tool ?? "tool"}`, command: JSON.stringify(item.arguments ?? {}), summary: item.error ? String(item.error) : `MCP ${item.status ?? "完成"}` };
        case "webSearch": return { tool: "web_search", command: item.query, summary: "网页搜索完成" };
        case "collabToolCall": return { tool: "subagent", command: item.prompt, summary: `子任务${item.status ?? "完成"}` };
        case "imageView": return { tool: "view_image", command: item.path, summary: "已查看图片" };
        case "contextCompaction": return { tool: "context_compaction", summary: "上下文压缩完成" };
        default: return undefined;
    }
}
function commandText(value) {
    if (Array.isArray(value))
        return value.map(commandText).filter(Boolean).join(" ");
    if (typeof value === "string")
        return value;
    if (typeof value === "number" || typeof value === "boolean")
        return String(value);
    if (!value || typeof value !== "object")
        return "";
    const command = value;
    const program = commandText(command.program ?? command.executable ?? command.file);
    const argumentsText = commandText(command.args ?? command.argv);
    if (program && argumentsText)
        return `${program} ${argumentsText}`;
    if (program)
        return program;
    for (const key of ["command", "commandLine", "cmd", "argv", "args", "program"]) {
        const nested = commandText(command[key]);
        if (nested)
            return nested;
    }
    return jsonText(value);
}
function firstText(...values) {
    for (const value of values) {
        const text = typeof value === "string" ? value.trim() : value === undefined || value === null ? "" : typeof value === "number" || typeof value === "boolean" ? String(value) : jsonText(value);
        if (text)
            return text;
    }
    return "";
}
function jsonText(value) {
    try {
        return JSON.stringify(value, null, 2) ?? "";
    }
    catch {
        return String(value);
    }
}
function filePaths(params, relatedItem) {
    const values = params?.paths ?? params?.files ?? params?.filePaths ?? params?.changes ?? params?.item?.changes ?? relatedItem?.changes;
    if (!Array.isArray(values))
        return firstText(params?.path, params?.filePath, params?.item?.path, relatedItem?.path);
    return values.map((value) => firstText(value?.path, value?.filePath, value?.relativePath, value?.file, value?.name, value)).filter(Boolean).join("\n");
}
function patchUpdateText(changes) {
    if (typeof changes === "string")
        return changes;
    if (!Array.isArray(changes)) {
        if (changes && typeof changes === "object") {
            const value = changes;
            const nested = patchUpdateText(value.changes ?? value.files ?? value.patches);
            return firstText(value.diff, value.patch, value.text) || nested;
        }
        return "";
    }
    const patches = changes.map((change) => firstText(change?.diff, change?.patch, change?.text)).filter(Boolean);
    if (patches.length > 0)
        return patches.join("\n");
    return changes.map((change) => firstText(change?.path, change?.filePath)).filter(Boolean).join("\n");
}
export function normalizeUserInputAnswers(questions, values) {
    const source = values ?? {};
    const fallback = source.answer;
    const result = {};
    if (!Array.isArray(questions))
        return result;
    for (const question of questions) {
        const id = String(question?.id ?? "");
        if (!id)
            continue;
        const value = source[id] ?? fallback;
        if (value === undefined || value === null)
            continue;
        result[id] = { answers: Array.isArray(value) ? value.map(String) : [String(value)] };
    }
    return result;
}
/**
 * Returns true while an agent message still looks like a structured JSON
 * result. App Server streams deltas before it sends item/completed, so the
 * first delta may be just `{`; holding the whole item prevents protocol JSON
 * from appearing in the Pi-style chat transcript.
 */
export function looksLikeStructuredResultPrefix(value) {
    let text = value.replace(/^\uFEFF/, "").trimStart();
    const fence = text.match(/^```(?:json)?\s*/i);
    if (fence) {
        text = text.slice(fence[0].length).trimStart();
        // A fence can arrive in a delta before the opening brace.
        if (!text)
            return true;
    }
    return text.startsWith("{");
}
/** Extract complete top-level JSON objects from prose, fenced JSON, or a
 * provider response that accidentally concatenated multiple objects. */
export function extractJsonObjects(value) {
    const objects = [];
    for (let start = 0; start < value.length; start++) {
        if (value[start] !== "{")
            continue;
        let depth = 0;
        let inString = false;
        let escaped = false;
        for (let index = start; index < value.length; index++) {
            const character = value[index];
            if (inString) {
                if (escaped)
                    escaped = false;
                else if (character === "\\")
                    escaped = true;
                else if (character === '"')
                    inString = false;
                continue;
            }
            if (character === '"') {
                inString = true;
                continue;
            }
            if (character === "{")
                depth++;
            else if (character === "}") {
                depth--;
                if (depth === 0) {
                    try {
                        objects.push(JSON.parse(value.slice(start, index + 1)));
                    }
                    catch { /* try the next object */ }
                    start = index;
                    break;
                }
            }
        }
    }
    return objects;
}
/** Parse the last valid CodeResult in a response. The last result wins when
 * Codex emits more than one structured message in a single turn. */
export function parseCodeResult(value) {
    let result;
    for (const candidate of extractJsonObjects(value)) {
        const parsed = validateCodeResult(candidate);
        if (parsed)
            result = parsed;
    }
    return result;
}
function failedResult(message) { return { status: "failed", summaryForUser: message, technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false }; }
function cancelledResult() { return { status: "cancelled", summaryForUser: "代码任务已取消。", technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false }; }
function redact(value) { return value.replace(/(sk-[A-Za-z0-9_-]{8,}|AIza[\w-]{20,}|Bearer\s+[A-Za-z0-9._~+\/-]{12,})/gi, "[已脱敏]"); }
//# sourceMappingURL=codex-worker.js.map