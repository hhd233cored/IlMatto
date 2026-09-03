import os from "node:os";
import path from "node:path";
import { mkdir, readFile, unlink, writeFile } from "node:fs/promises";
import { AntigravitySession, extractManagerMessage } from "./antigravity.js";
import { managerActionSchema, managerPolicyPrompt } from "./runtime.js";
import { validateManagerAction } from "./protocol.js";
import { buildImageAwareCompanionPrompt, stageManagedImages } from "./image-staging.js";
export class AntigravityCoordinator {
    runtime;
    sessionId;
    source = "antigravity";
    session;
    constructor(config, runtime, sessionId = runtime.agentName) {
        this.runtime = runtime;
        this.sessionId = sessionId;
        this.session = new AntigravitySession(config.executable?.trim() || "agy", runtime, 
        // Antigravity turns are intentionally unbounded. Cancellation and
        // process shutdown remain the explicit lifecycle escape hatches.
        0, config.effort ?? "medium", config.model?.trim() || undefined, config.conversationId, undefined, sessionId);
    }
    get sessionRef() { return this.session.activeConversationId; }
    async ask(userMessage, onStream, attachments = []) {
        const staged = await stageManagedImages(this.runtime, this.sessionId, attachments);
        const prompt = staged.length > 0 ? buildImageAwareCompanionPrompt(userMessage, staged) : userMessage;
        const turn = await this.session.ask(prompt, onStream, staged.map((image) => image.runtimePath));
        return { action: turn.action, sessionRef: turn.conversationId ?? this.sessionRef, cacheReadTokens: turn.cacheReadTokens };
    }
    /**
     * Converts a presentation-safe Coding Worker update into the companion's
     * voice. The caller supplies only bounded facts; the coordinator never sees
     * the worker's reasoning, source, diff, command line or command output.
     */
    async narrate(updatePrompt, onStream) {
        const turn = await this.session.ask(updatePrompt, onStream);
        return turn.action.action === "respond" ? turn.action.message : "";
    }
    cancel() { this.session.cancel(); }
    dispose() { this.session.dispose(); }
}
export class OpenAICompatibleCoordinator {
    config;
    source = "api_manager";
    history = [];
    loaded = false;
    abort;
    file;
    systemPrompt;
    constructor(config, sessionId, systemPrompt = managerPolicyPrompt) {
        this.config = config;
        this.file = resolveCoordinatorSessionFile(config.sessionFile, sessionId);
        this.systemPrompt = systemPrompt;
    }
    get sessionRef() { return this.file; }
    async ask(userMessage, onStream, _attachments = []) {
        await this.load();
        this.abort = new AbortController();
        const user = { role: "user", content: userMessage, createdAt: new Date().toISOString() };
        const requestHistory = compactHistory([...this.history, user]);
        try {
            let response;
            try {
                response = await this.request(requestHistory, "json_schema", onStream);
            }
            catch (error) {
                if (!isStructuredOutputUnsupported(error))
                    throw error;
                response = await this.request(requestHistory, "json_object", onStream);
            }
            let action = parseAction(response.text);
            if (!action) {
                const repaired = await this.request([...requestHistory, { role: "system", content: "The prior output was invalid. Return only one valid ManagerAction JSON object.", createdAt: new Date().toISOString() }], "json_object", onStream);
                action = parseAction(repaired.text);
                response = repaired;
            }
            if (!action)
                throw new CoordinatorError("MAIN_API_SCHEMA_INVALID", "主 Agent API 连续两次返回无效的 ManagerAction JSON。");
            this.history = compactHistory([...this.history, user, { role: "assistant", content: JSON.stringify(action), createdAt: new Date().toISOString() }]);
            await this.save();
            return {
                action, sessionRef: this.file, contextTokens: response.promptTokens,
                contextWindow: response.contextWindow, cacheReadTokens: response.cachedTokens,
            };
        }
        finally {
            this.abort = undefined;
        }
    }
    cancel() { this.abort?.abort(); }
    dispose() { this.cancel(); }
    async load() {
        if (this.loaded)
            return;
        this.loaded = true;
        try {
            const text = await readFile(this.file, "utf8");
            this.history = text.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line)).filter(isHistoryRecord);
        }
        catch (error) {
            if (error?.code !== "ENOENT")
                throw new CoordinatorError("MAIN_API_SESSION_INVALID", "主 Agent API 会话文件损坏或无法读取。");
        }
    }
    async save() {
        await mkdir(path.dirname(this.file), { recursive: true });
        await writeFile(this.file, this.history.map((entry) => JSON.stringify(entry)).join("\n") + "\n", "utf8");
    }
    async request(history, format, onStream) {
        const endpoint = `${normalizeBaseUrl(this.config.baseUrl)}/chat/completions`;
        const responseFormat = format === "json_schema"
            ? { type: "json_schema", json_schema: { name: "ilmatto_manager_action", strict: true, schema: managerActionSchema } }
            : { type: "json_object" };
        const timeout = setTimeout(() => this.abort?.abort(), Math.min(600, Math.max(10, this.config.timeoutSeconds ?? 120)) * 1000);
        try {
            const response = await fetch(endpoint, {
                method: "POST", signal: this.abort?.signal,
                headers: { "content-type": "application/json", ...(this.config.apiKey ? { authorization: `Bearer ${this.config.apiKey}` } : {}) },
                body: JSON.stringify({
                    model: this.config.modelId,
                    messages: [{ role: "system", content: this.systemPrompt }, ...history.map(({ role, content }) => ({ role, content }))],
                    response_format: responseFormat,
                    stream: Boolean(onStream),
                }),
            });
            if (!response.ok) {
                const raw = await response.text();
                const code = response.status === 401 || response.status === 403 ? "MAIN_API_AUTH_FAILED" : "MAIN_API_ERROR";
                throw new CoordinatorError(code, redactApiError(`主 Agent API HTTP ${response.status}：${raw.slice(0, 2_000)}`, this.config.apiKey), response.status);
            }
            if (onStream && response.body)
                return await readStreamingApiResponse(response, onStream);
            const raw = await response.text();
            let payload;
            try {
                payload = JSON.parse(raw);
            }
            catch {
                throw new CoordinatorError("MAIN_API_SCHEMA_INVALID", "主 Agent API 返回了无效 JSON。");
            }
            const content = payload?.choices?.[0]?.message?.content;
            const text = typeof content === "string" ? content : Array.isArray(content) ? content.map((item) => item?.text ?? "").join("") : "";
            if (!text)
                throw new CoordinatorError("MAIN_API_SCHEMA_INVALID", "主 Agent API 没有返回消息内容。");
            return {
                text,
                promptTokens: numberOrUndefined(payload?.usage?.prompt_tokens),
                cachedTokens: numberOrUndefined(payload?.usage?.prompt_tokens_details?.cached_tokens ?? payload?.usage?.cached_tokens),
                contextWindow: numberOrUndefined(payload?.usage?.context_window),
            };
        }
        catch (error) {
            if (error instanceof CoordinatorError)
                throw error;
            if (error?.name === "AbortError")
                throw new CoordinatorError("MAIN_API_TIMEOUT", "主 Agent API 请求已超时或取消。");
            throw new CoordinatorError("MAIN_API_ERROR", redactApiError(error instanceof Error ? error.message : "主 Agent API 请求失败。", this.config.apiKey));
        }
        finally {
            clearTimeout(timeout);
        }
    }
}
async function readStreamingApiResponse(response, onStream) {
    if (!response.body)
        throw new CoordinatorError("MAIN_API_ERROR", "主 Agent API 没有返回可读取的流。", response.status);
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let pending = "";
    let content = "";
    let emittedMessage = "";
    let promptTokens;
    let cachedTokens;
    let contextWindow;
    const consume = (line) => {
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith(":"))
            return;
        const data = trimmed.startsWith("data:") ? trimmed.slice(5).trim() : trimmed;
        if (!data || data === "[DONE]")
            return;
        let payload;
        try {
            payload = JSON.parse(data);
        }
        catch {
            return;
        }
        const usage = payload?.usage;
        promptTokens ??= numberOrUndefined(usage?.prompt_tokens ?? usage?.input_tokens);
        cachedTokens ??= numberOrUndefined(usage?.prompt_tokens_details?.cached_tokens ?? usage?.input_tokens_details?.cached_tokens ?? usage?.cached_tokens ?? usage?.cache_read_tokens);
        contextWindow ??= numberOrUndefined(usage?.context_window);
        const delta = payload?.choices?.[0]?.delta ?? {};
        const reasoning = [delta.reasoning_content, delta.reasoning, delta.thinking]
            .find((value) => typeof value === "string" && value.length > 0);
        if (typeof reasoning === "string")
            onStream({ kind: "thinking", text: reasoning });
        const rawContent = delta.content ?? payload?.choices?.[0]?.message?.content;
        const text = typeof rawContent === "string"
            ? rawContent
            : Array.isArray(rawContent) ? rawContent.map((item) => typeof item === "string" ? item : item?.text ?? "").join("") : "";
        if (!text)
            return;
        content += text;
        const extracted = extractManagerMessage(content);
        if (extracted.found) {
            if (extracted.text.startsWith(emittedMessage)) {
                const suffix = extracted.text.slice(emittedMessage.length);
                emittedMessage = extracted.text;
                if (suffix)
                    onStream({ kind: "text", text: suffix });
            }
        }
        else if (!looksLikeStructuredManagerOutput(content)) {
            onStream({ kind: "text", text });
        }
    };
    while (true) {
        const chunk = await reader.read();
        if (chunk.done)
            break;
        pending += decoder.decode(chunk.value, { stream: true });
        const lines = pending.split(/\r?\n/);
        pending = lines.pop() ?? "";
        for (const line of lines)
            consume(line);
    }
    pending += decoder.decode();
    if (pending.trim())
        consume(pending);
    if (!content)
        throw new CoordinatorError("MAIN_API_SCHEMA_INVALID", "主 Agent API 流没有返回消息内容。", response.status);
    return { text: content, promptTokens, cachedTokens, contextWindow };
}
function looksLikeStructuredManagerOutput(value) {
    const trimmed = value.trimStart();
    return trimmed.startsWith("{") || trimmed.startsWith("[") || trimmed.startsWith("```") || /\"(?:schemaVersion|action|message)\"\s*:/.test(value);
}
export class CoordinatorError extends Error {
    code;
    status;
    constructor(code, message, status) {
        super(message);
        this.code = code;
        this.status = status;
    }
}
export async function deleteCoordinatorSessionFile(value) {
    if (!value)
        return;
    const file = resolveCoordinatorSessionFile(value, path.basename(value, ".jsonl"));
    try {
        await unlink(file);
    }
    catch (error) {
        if (error?.code !== "ENOENT")
            throw new CoordinatorError("MAIN_API_SESSION_DELETE_FAILED", "无法删除主 Agent API 会话文件。");
    }
}
export function probeToStatus(probe) {
    return { available: probe.available, authenticated: probe.authenticated, version: probe.version, message: probe.message };
}
function resolveCoordinatorSessionFile(candidate, sessionId) {
    const localAppData = process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local");
    const directory = path.resolve(process.env.ILMATTO_COORDINATOR_SESSION_DIR ?? path.join(localAppData, "IlMatto", "manager-sessions", "coordinator"));
    const file = path.resolve(candidate || path.join(directory, `${safeSessionId(sessionId)}.jsonl`));
    if (path.dirname(file).toLowerCase() !== directory.toLowerCase() || path.extname(file).toLowerCase() !== ".jsonl") {
        throw new CoordinatorError("MAIN_API_SESSION_INVALID", "主 Agent API 会话文件不在 IlMatto coordinator 目录中。");
    }
    return file;
}
function safeSessionId(value) { return value.replace(/[^a-zA-Z0-9_-]/g, "").slice(0, 128) || crypto.randomUUID(); }
function normalizeBaseUrl(value) { return value.trim().replace(/^[`"']|[`"']$/g, "").replace(/\/+$/, "").replace(/\/chat\/completions$/i, ""); }
function parseAction(text) {
    const trimmed = text.trim().replace(/^```(?:json)?\s*/i, "").replace(/\s*```$/, "");
    try {
        return validateManagerAction(JSON.parse(trimmed));
    }
    catch {
        return undefined;
    }
}
function isHistoryRecord(value) { return value && ["user", "assistant", "system"].includes(value.role) && typeof value.content === "string"; }
function compactHistory(history) {
    const characters = history.reduce((sum, item) => sum + item.content.length, 0);
    if (history.length <= 80 && characters <= 48_000)
        return history;
    const recent = history.slice(-24);
    const older = history.slice(0, -24);
    const summary = older.map((item) => `${item.role}: ${item.content.replace(/\s+/g, " ").slice(0, 240)}`).join("\n").slice(-8_000);
    return [{ role: "system", content: `Earlier coordinator history was compacted locally:\n${summary}`, createdAt: new Date().toISOString() }, ...recent];
}
function isStructuredOutputUnsupported(error) {
    return error instanceof CoordinatorError && error.status === 400 && /response.?format|json.?schema|unsupported|unknown/i.test(error.message);
}
function redactApiError(value, apiKey) {
    const generic = value.replace(/(sk-[A-Za-z0-9_-]{8,}|AIza[\w-]{20,}|Bearer\s+[A-Za-z0-9._~+\/-]{12,})/gi, "[已脱敏]");
    return apiKey ? generic.split(apiKey).join("[已脱敏]") : generic;
}
function numberOrUndefined(value) { const result = Number(value); return Number.isFinite(result) ? result : undefined; }
//# sourceMappingURL=coordinator.js.map