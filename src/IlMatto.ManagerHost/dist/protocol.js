export const codeResultSchema = {
    type: "object",
    additionalProperties: false,
    properties: {
        status: { type: "string", enum: ["completed", "blocked", "failed", "cancelled"] },
        summaryForUser: { type: "string" },
        technicalDecisions: {
            type: "array",
            items: { type: "object", additionalProperties: false, properties: { decision: { type: "string" }, reason: { type: "string" } }, required: ["decision", "reason"] },
        },
        filesChanged: {
            type: "array",
            items: { type: "object", additionalProperties: false, properties: { path: { type: "string" }, additions: { type: "integer", minimum: 0 }, deletions: { type: "integer", minimum: 0 } }, required: ["path", "additions", "deletions"] },
        },
        validation: {
            type: "array",
            items: { type: "object", additionalProperties: false, properties: { command: { type: "string" }, status: { type: "string", enum: ["passed", "failed", "skipped"] }, summary: { type: "string" } }, required: ["command", "status", "summary"] },
        },
        questions: { type: "array", items: { type: "string" } },
        needsUserDecision: { type: "boolean" },
    },
    required: ["status", "summaryForUser", "technicalDecisions", "filesChanged", "validation", "questions", "needsUserDecision"],
};
export function isManagerClientMessage(value) {
    if (!value || typeof value !== "object")
        return false;
    const message = value;
    const session = typeof message.sessionId === "string" && message.sessionId.length > 0;
    switch (message.type) {
        case "start_manager_session":
            if (!session || typeof message.workspacePath !== "string")
                return false;
            if (message.mainAgent !== undefined && !isMainAgentConfig(message.mainAgent))
                return false;
            if (message.codingAgent !== undefined && !isCodingAgentConfig(message.codingAgent))
                return false;
            if (message.antigravity !== undefined && !isUnifiedAntigravityConfig(message.antigravity))
                return false;
            if (message.executorProfiles !== undefined && !isExecutorProfiles(message.executorProfiles))
                return false;
            if (message.companionProfile !== undefined && !isCompanionProfile(message.companionProfile))
                return false;
            if (message.conversationHistory !== undefined &&
                (!Array.isArray(message.conversationHistory) || !message.conversationHistory.every(isCompanionHistoryItem)))
                return false;
            return Boolean(message.antigravity) || Boolean(message.mainAgent && message.codingAgent) || (typeof message.baseUrl === "string" && typeof message.modelId === "string");
        case "activate_manager_session":
            return session;
        case "send_manager_message": {
            const attachments = message.attachments;
            return session && typeof message.text === "string" &&
                (message.text.length > 0 || (Array.isArray(attachments) && attachments.length > 0)) &&
                (message.executor === undefined || isCodingProvider(message.executor)) &&
                (attachments === undefined || (Array.isArray(attachments) && attachments.every(isManagerImageAttachment))) &&
                (message.generateTitle === undefined || typeof message.generateTitle === "boolean");
        }
        case "approve_coding_tool": return session && typeof message.callId === "string" && typeof message.approved === "boolean";
        case "companion_memory_request":
            return isCompanionMemoryRequest(message, session);
        case "cancel_manager_turn": return session && (message.target === undefined || message.target === "antigravity" || message.target === "all");
        case "request_verification": return session && typeof message.taskId === "string" && message.taskId.length > 0;
        case "list_agent_models": return session && message.provider === "antigravity";
        case "delete_manager_session": return session;
        case "shutdown": return message.sessionId === undefined || typeof message.sessionId === "string";
        default: return false;
    }
}
export function isCompanionProfile(value) {
    if (!value || typeof value !== "object")
        return false;
    const item = value;
    return typeof item.characterPrompt === "string" &&
        (item.characterName === undefined || typeof item.characterName === "string") &&
        (item.userProfile === undefined || typeof item.userProfile === "string") &&
        (item.relationshipSummary === undefined || typeof item.relationshipSummary === "string");
}
function isCompanionMemoryRequest(value, session) {
    if (!session || typeof value.requestId !== "string" || !value.requestId ||
        !["session_search", "session_open", "session_update", "profile_update"].includes(String(value.operation)))
        return false;
    if (value.query !== undefined && typeof value.query !== "string")
        return false;
    if (value.targetSessionId !== undefined && typeof value.targetSessionId !== "string")
        return false;
    if (value.limit !== undefined && (typeof value.limit !== "number" || !Number.isInteger(value.limit) || value.limit < 1 || value.limit > 5))
        return false;
    if (value.patch !== undefined && !isSessionSummaryPatch(value.patch))
        return false;
    if (value.profilePatch !== undefined && !isProfilePatch(value.profilePatch))
        return false;
    return true;
}
function isSessionSummaryPatch(value) {
    if (!value || typeof value !== "object" || Array.isArray(value))
        return false;
    const item = value;
    return (item.title === undefined || typeof item.title === "string") &&
        (item.summaryPatch === undefined || typeof item.summaryPatch === "string") &&
        isOptionalStringArray(item.keyEvents) && isOptionalStringArray(item.openLoops) && isOptionalStringArray(item.keywords);
}
function isProfilePatch(value) {
    if (!value || typeof value !== "object" || Array.isArray(value))
        return false;
    const item = value;
    return isProfileSection(item.section) && isOptionalStringArray(item.add) && isOptionalStringArray(item.remove);
}
function isProfileSection(value) {
    return value === "basic" || value === "interests" || value === "preferences" || value === "boundaries" || value === "current_topics";
}
function isOptionalStringArray(value) {
    return value === undefined || (Array.isArray(value) && value.every((item) => typeof item === "string"));
}
/** Validate the compact single-session start payload without requiring any of
 * the legacy mainAgent/codingAgent fields.  Keeping this check at the pipe
 * boundary prevents malformed optional values from reaching the CLI spawn
 * path while preserving backwards compatibility for old desktop payloads. */
export function isUnifiedAntigravityConfig(value) {
    if (!value || typeof value !== "object" || Array.isArray(value))
        return false;
    const item = value;
    return (item.provider === undefined || item.provider === "antigravity") &&
        (item.executable === undefined || typeof item.executable === "string") &&
        (item.conversationId === undefined || typeof item.conversationId === "string") &&
        (item.model === undefined || typeof item.model === "string") &&
        (item.effort === undefined || item.effort === "low" || item.effort === "medium" || item.effort === "high") &&
        (item.toolPermission === undefined || isAntigravityToolPermission(item.toolPermission)) &&
        (item.terminalSandbox === undefined || typeof item.terminalSandbox === "boolean");
}
export function isCompanionHistoryItem(value) {
    if (!value || typeof value !== "object")
        return false;
    const item = value;
    return (item.role === "user" || item.role === "assistant") && typeof item.text === "string";
}
export function isManagerImageAttachment(value) {
    if (!value || typeof value !== "object")
        return false;
    const item = value;
    return item.type === "image" && typeof item.path === "string" && item.path.trim().length > 0 &&
        (item.mimeType === undefined || typeof item.mimeType === "string") &&
        (item.displayName === undefined || typeof item.displayName === "string") &&
        (item.attachmentId === undefined || typeof item.attachmentId === "string") &&
        (item.order === undefined || (typeof item.order === "number" && Number.isInteger(item.order) && item.order >= 0));
}
/** Normalize legacy attachments at the host seam without changing the persisted desktop data. */
export function normalizeManagerImageAttachments(attachments) {
    return (attachments ?? []).map((attachment, index) => ({
        ...attachment,
        attachmentId: attachment.attachmentId?.trim() || `legacy-${index}`,
        displayName: attachment.displayName?.trim() || attachment.path.split(/[\\/]/).pop() || `image-${index + 1}`,
        order: attachment.order ?? index,
    })).sort((left, right) => (left.order ?? 0) - (right.order ?? 0));
}
export function isMainAgentConfig(value) {
    if (!value || typeof value !== "object")
        return false;
    const item = value;
    if (item.provider === "antigravity") {
        return (item.transport === undefined || item.transport === "sdk" || item.transport === "cli" || item.transport === "cli_interactive") &&
            (item.conversationId === undefined || typeof item.conversationId === "string") &&
            (item.sdkSessionRef === undefined || typeof item.sdkSessionRef === "string") &&
            (item.legacyCliConversationId === undefined || typeof item.legacyCliConversationId === "string") &&
            (item.model === undefined || typeof item.model === "string") &&
            (item.effort === undefined || item.effort === "low" || item.effort === "medium" || item.effort === "high") &&
            (item.toolPermission === undefined || isAntigravityToolPermission(item.toolPermission)) &&
            (item.terminalSandbox === undefined || typeof item.terminalSandbox === "boolean");
    }
    return item.provider === "openai_compatible" && typeof item.baseUrl === "string" && typeof item.modelId === "string";
}
export function isCodingAgentConfig(value) {
    if (!value || typeof value !== "object")
        return false;
    const item = value;
    if (item.provider === "antigravity")
        return item.executionPolicy === undefined || ["approval", "safe_tests", "autonomous"].includes(String(item.executionPolicy));
    return item.provider === "pi" && typeof item.baseUrl === "string" && typeof item.modelId === "string";
}
export function isAntigravityToolPermission(value) {
    return value === "request-review" || value === "proceed-in-sandbox" || value === "always-proceed" || value === "strict";
}
export function isCodingProvider(value) {
    return value === "antigravity" || value === "pi";
}
/** Select the reusable CLI conversation id from both the current and legacy
 * start-message shapes. SDK-only references are intentionally ignored. */
export function resolveAntigravityConversationId(message) {
    if (message.mainAgent?.provider === "antigravity") {
        if (message.mainAgent.transport === "sdk")
            return firstNonEmpty(message.mainAgent.legacyCliConversationId);
        return firstNonEmpty(message.mainAgent.conversationId, message.mainAgent.legacyCliConversationId);
    }
    return firstNonEmpty(message.antigravity?.conversationId, message.agyConversationId);
}
function firstNonEmpty(...values) {
    return values.map((value) => value?.trim()).find((value) => Boolean(value));
}
export function isExecutorProfiles(value) {
    if (!value || typeof value !== "object" || Array.isArray(value))
        return false;
    return Object.entries(value).every(([provider, config]) => isCodingProvider(provider) && isCodingAgentConfig(config) && config.provider === provider);
}
export function validateManagerAction(value) {
    if (!value || typeof value !== "object")
        return undefined;
    const item = value;
    if (item.schemaVersion !== 1 || !["delegate_code", "respond", "ask_user"].includes(String(item.action)) || typeof item.message !== "string")
        return undefined;
    return { schemaVersion: 1, action: item.action, message: item.message };
}
export function validateCodeResult(value) {
    if (!value || typeof value !== "object")
        return undefined;
    const item = value;
    if (!["completed", "blocked", "failed", "cancelled"].includes(String(item.status)) || typeof item.summaryForUser !== "string" || typeof item.needsUserDecision !== "boolean")
        return undefined;
    if (!Array.isArray(item.technicalDecisions) || !item.technicalDecisions.every((entry) => entry && typeof entry.decision === "string" && typeof entry.reason === "string"))
        return undefined;
    if (!Array.isArray(item.filesChanged) || !item.filesChanged.every((entry) => entry && typeof entry.path === "string" && Number.isInteger(entry.additions) && entry.additions >= 0 && Number.isInteger(entry.deletions) && entry.deletions >= 0))
        return undefined;
    if (!Array.isArray(item.validation) || !item.validation.every((entry) => entry && typeof entry.command === "string" && ["passed", "failed", "skipped"].includes(String(entry.status)) && typeof entry.summary === "string"))
        return undefined;
    if (!Array.isArray(item.questions) || !item.questions.every((entry) => typeof entry === "string"))
        return undefined;
    return item;
}
export function normalizeStartConfig(message) {
    if (message.antigravity) {
        const antigravity = {
            provider: "antigravity",
            transport: "cli",
            executable: message.antigravity.executable ?? message.agyPath,
            conversationId: message.antigravity.conversationId,
            legacyCliConversationId: undefined,
            model: normalizeAntigravityModelId(message.antigravity.model ?? message.agyModel),
            effort: message.antigravity.effort ?? message.effort,
            timeoutSeconds: 0,
            toolPermission: message.antigravity.toolPermission,
            terminalSandbox: message.antigravity.terminalSandbox,
        };
        const codingAgent = {
            provider: "antigravity",
            executable: antigravity.executable,
            conversationId: antigravity.conversationId,
            model: antigravity.model,
            effort: antigravity.effort,
            executionPolicy: "autonomous",
        };
        return { mainAgent: antigravity, codingAgent, executorProfiles: { antigravity: codingAgent } };
    }
    if (message.mainAgent && message.codingAgent) {
        const mainAgent = message.mainAgent.provider === "antigravity"
            ? { ...message.mainAgent, transport: message.mainAgent.transport ?? "cli", model: normalizeAntigravityModelId(message.mainAgent.model) }
            : message.mainAgent;
        return { mainAgent, codingAgent: message.codingAgent, executorProfiles: { ...(message.executorProfiles ?? {}), [message.codingAgent.provider]: message.codingAgent } };
    }
    return {
        mainAgent: { provider: "antigravity", transport: "cli", executable: message.agyPath, conversationId: message.agyConversationId, legacyCliConversationId: message.agyConversationId, model: normalizeAntigravityModelId(message.agyModel), effort: message.effort, timeoutSeconds: message.timeoutSeconds },
        codingAgent: { provider: "pi", baseUrl: message.baseUrl ?? "", modelId: message.modelId ?? "", apiKey: message.apiKey, sessionFile: message.piSessionFile, autoApproveSafeCommands: message.autoApproveSafeCommands, autoApproveGitOperations: message.autoApproveGitOperations },
        executorProfiles: { pi: { provider: "pi", baseUrl: message.baseUrl ?? "", modelId: message.modelId ?? "", apiKey: message.apiKey, sessionFile: message.piSessionFile, autoApproveSafeCommands: message.autoApproveSafeCommands, autoApproveGitOperations: message.autoApproveGitOperations } },
    };
}
/** `agy models` prints a stable slug followed by a human-readable label.
 * Persisting that whole display line (including its tab separator) makes AGY
 * treat the model selection as invalid, so use the slug field consistently. */
export function normalizeAntigravityModelId(value) {
    // The CLI may mark the selected model with a leading `*`.  Strip that
    // marker before taking the first token; otherwise a line such as
    // `* gemini-...  Display Name` would normalize to an empty id.
    const text = (value ?? "").trim().replace(/\\t/g, " ").replace(/^(?:[*•]\s*)+/, "");
    if (!text)
        return undefined;
    const token = text.split(/\s+/)[0].trim();
    return token || undefined;
}
//# sourceMappingURL=protocol.js.map