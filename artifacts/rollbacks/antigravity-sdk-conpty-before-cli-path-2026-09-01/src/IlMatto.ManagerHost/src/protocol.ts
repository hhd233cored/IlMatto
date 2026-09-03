export type ManagerAction = {
  schemaVersion: 1;
  action: "delegate_code" | "respond" | "ask_user";
  message: string;
};

export type CompanionProfile = {
  characterPrompt: string;
  userProfile: string;
  relationshipSummary: string;
};

export type ManagerImageAttachment = {
  type: "image";
  /** Stable within a persisted IlMatto transcript. Optional on legacy wire messages. */
  attachmentId?: string;
  path: string;
  mimeType?: string;
  displayName?: string;
  /** Preserve the order in which the user attached images. */
  order?: number;
};

export type AntigravityTransport = "sdk" | "cli" | "cli_interactive";

export type CompanionHistoryItem = {
  role: "user" | "assistant";
  text: string;
};

export type CodeResult = {
  status: "completed" | "blocked" | "failed" | "cancelled";
  summaryForUser: string;
  technicalDecisions: Array<{ decision: string; reason: string }>;
  filesChanged: Array<{ path: string; additions: number; deletions: number }>;
  validation: Array<{ command: string; status: "passed" | "failed" | "skipped"; summary: string }>;
  questions: string[];
  needsUserDecision: boolean;
};

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
} as const;

export type MainAgentConfig =
  | { provider: "antigravity"; transport?: AntigravityTransport; executable?: string; conversationId?: string; sdkSessionRef?: string; legacyCliConversationId?: string; model?: string; effort?: "low" | "medium" | "high"; timeoutSeconds?: number }
  | { provider: "openai_compatible"; baseUrl: string; modelId: string; apiKey?: string; sessionFile?: string; timeoutSeconds?: number };

export type CodingAgentConfig =
  | { provider: "pi"; baseUrl: string; modelId: string; apiKey?: string; sessionFile?: string; autoApproveSafeCommands?: boolean; autoApproveGitOperations?: boolean }
  | { provider: "codex"; executable?: string; threadId?: string; model?: string; effort?: string };

export type CodingInteractionKind = "command_approval" | "file_approval" | "permissions" | "question" | "mcp_form" | "mcp_url";

export type ManagerClientMessage =
  | {
      type: "start_manager_session"; sessionId: string; workspacePath: string;
      mainAgent?: MainAgentConfig; codingAgent?: CodingAgentConfig; awaitingCodingDecision?: boolean;
      companionProfile?: CompanionProfile; conversationHistory?: CompanionHistoryItem[];
      agyPath?: string; agyConversationId?: string; piSessionFile?: string; awaitingPiDecision?: boolean;
      baseUrl?: string; modelId?: string; apiKey?: string; autoApproveSafeCommands?: boolean; autoApproveGitOperations?: boolean;
      agyModel?: string; effort?: "low" | "medium" | "high"; timeoutSeconds?: number;
    }
  | { type: "send_manager_message"; sessionId: string; text: string; attachments?: ManagerImageAttachment[] }
  | {
      type: "interactive_cli_response"; sessionId: string; requestId: string;
      event: "ready" | "output" | "paste_dispatched" | "input_written" | "submitted" | "clipboard_released" | "exited" | "error";
      text?: string; conversationId?: string; code?: string; message?: string;
    }
  | { type: "approve_coding_tool"; sessionId: string; callId: string; approved: boolean }
  | { type: "resolve_coding_interaction"; sessionId: string; requestId: string; approved: boolean; values?: Record<string, unknown> }
  | { type: "cancel_manager_turn"; sessionId: string }
  | { type: "probe_codex"; sessionId: string; executable?: string; workspacePath?: string }
  | { type: "start_codex_login"; sessionId: string; executable?: string; workspacePath?: string }
  | { type: "delete_manager_session"; sessionId: string; workspacePath?: string; mainAgent?: MainAgentConfig; codingAgent?: CodingAgentConfig; piSessionFile?: string; coordinatorSessionFile?: string; codexThreadId?: string }
  | { type: "shutdown"; sessionId?: string };

export type ManagerHostMessage =
  | { type: "manager_host_ready"; version: string }
  | {
      type: "interactive_cli_request"; sessionId: string; requestId: string;
      operation: "start" | "paste_image" | "write_text" | "submit" | "cancel" | "release_clipboard" | "shutdown";
      executable?: string; workingDirectory?: string; conversationId?: string;
      model?: string; effort?: string; agentName?: string; logPath?: string;
      text?: string; attachmentPath?: string;
    }
  | { type: "manager_session_ready"; sessionId: string; mainProvider: MainAgentConfig["provider"]; codingProvider: CodingAgentConfig["provider"]; mainSessionRef?: string; codingSessionRef?: string; agyConversationId?: string; piSessionFile?: string; antigravityAvailable?: boolean; authenticated?: boolean; version?: string; antigravityTransport?: AntigravityTransport }
  | { type: "provider_status"; sessionId: string; layer: "main" | "coding"; provider: MainAgentConfig["provider"] | CodingAgentConfig["provider"]; available: boolean; authenticated?: boolean; version?: string; message?: string; policy?: string }
  | { type: "antigravity_status"; sessionId: string; available: boolean; authenticated: boolean; version?: string; message?: string }
  | { type: "manager_state"; sessionId: string; state: "idle" | "routing" | "responding" | "coding" | "waiting_approval" | "cancelled" | "error" }
  | { type: "manager_delta"; sessionId: string; source: "antigravity" | "api_manager"; text: string }
  | { type: "manager_thinking_delta"; sessionId: string; source: "antigravity" | "api_manager"; text: string }
  | { type: "manager_completed"; sessionId: string; source: "antigravity" | "api_manager"; text: string; action: "delegate_code" | "respond" | "ask_user"; final?: boolean }
  | { type: "manager_metrics"; sessionId: string; provider: "antigravity" | "api_manager"; contextTokens?: number; contextWindow?: number; cacheReadTokens?: number; antigravityCacheReadTokens?: number }
  | { type: "delegation_started"; sessionId: string; taskId: string; provider: "pi" | "codex" }
  | { type: "coding_delta"; sessionId: string; taskId?: string; source: "pi" | "codex"; text: string }
  | { type: "coding_thinking_delta"; sessionId: string; taskId?: string; source: "pi" | "codex"; text: string }
  | { type: "coding_tool_approval_request"; sessionId: string; callId: string; tool: string; summary: string; details: string; diff?: string }
  | { type: "coding_interaction_request"; sessionId: string; requestId: string; provider: "pi" | "codex"; kind: CodingInteractionKind; title: string; details: string; diff?: string; fields?: unknown; url?: string }
  | { type: "coding_interaction_completed"; sessionId: string; requestId: string; provider: "pi" | "codex" }
  | { type: "coding_tool_started"; sessionId: string; callId: string; tool: string; command?: string }
  | { type: "coding_tool_output"; sessionId: string; callId: string; tool: string; text: string }
  | { type: "coding_tool_completed"; sessionId: string; callId: string; tool: string; ok: boolean; summary: string; output?: string; diff?: string; autoApproved?: boolean }
  | { type: "code_result"; sessionId: string; taskId: string; result: CodeResult }
  | { type: "codex_account_status"; sessionId: string; available: boolean; authenticated: boolean; version?: string; models?: Array<{ id: string; displayName: string; efforts: string[] }>; policy?: string; message?: string }
  | { type: "codex_login_started"; sessionId: string; loginId: string; url: string }
  | { type: "codex_login_completed"; sessionId: string; loginId?: string; ok: boolean; message?: string }
  | { type: "manager_session_deleted"; sessionId: string }
  | { type: "manager_error"; sessionId?: string; code: string; message: string };

export function isManagerClientMessage(value: unknown): value is ManagerClientMessage {
  if (!value || typeof value !== "object") return false;
  const message = value as Record<string, unknown>;
  const session = typeof message.sessionId === "string" && message.sessionId.length > 0;
  switch (message.type) {
    case "start_manager_session":
      if (!session || typeof message.workspacePath !== "string") return false;
      if (message.mainAgent !== undefined && !isMainAgentConfig(message.mainAgent)) return false;
      if (message.codingAgent !== undefined && !isCodingAgentConfig(message.codingAgent)) return false;
      if (message.companionProfile !== undefined && !isCompanionProfile(message.companionProfile)) return false;
      if (message.conversationHistory !== undefined &&
          (!Array.isArray(message.conversationHistory) || !message.conversationHistory.every(isCompanionHistoryItem))) return false;
      return Boolean(message.mainAgent && message.codingAgent) || (typeof message.baseUrl === "string" && typeof message.modelId === "string");
    case "send_manager_message": {
      const attachments = message.attachments;
      return session && typeof message.text === "string" &&
        (message.text.length > 0 || (Array.isArray(attachments) && attachments.length > 0)) &&
        (attachments === undefined || (Array.isArray(attachments) && attachments.every(isManagerImageAttachment)));
    }
    case "interactive_cli_response":
      return session && typeof message.requestId === "string" && message.requestId.length > 0 &&
        ["ready", "output", "paste_dispatched", "input_written", "submitted", "clipboard_released", "exited", "error"].includes(String(message.event)) &&
        (message.text === undefined || typeof message.text === "string") &&
        (message.conversationId === undefined || typeof message.conversationId === "string") &&
        (message.code === undefined || typeof message.code === "string") &&
        (message.message === undefined || typeof message.message === "string");
    case "approve_coding_tool": return session && typeof message.callId === "string" && typeof message.approved === "boolean";
    case "resolve_coding_interaction": return session && typeof message.requestId === "string" && typeof message.approved === "boolean";
    case "cancel_manager_turn": return session;
    case "probe_codex": return session && (message.executable === undefined || typeof message.executable === "string");
    case "start_codex_login": return session && (message.executable === undefined || typeof message.executable === "string");
    case "delete_manager_session": return session;
    case "shutdown": return message.sessionId === undefined || typeof message.sessionId === "string";
    default: return false;
  }
}

export function isCompanionProfile(value: unknown): value is CompanionProfile {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  return typeof item.characterPrompt === "string" && typeof item.userProfile === "string" && typeof item.relationshipSummary === "string";
}

export function isCompanionHistoryItem(value: unknown): value is CompanionHistoryItem {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  return (item.role === "user" || item.role === "assistant") && typeof item.text === "string";
}

export function isManagerImageAttachment(value: unknown): value is ManagerImageAttachment {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  return item.type === "image" && typeof item.path === "string" && item.path.trim().length > 0 &&
    (item.mimeType === undefined || typeof item.mimeType === "string") &&
    (item.displayName === undefined || typeof item.displayName === "string") &&
    (item.attachmentId === undefined || typeof item.attachmentId === "string") &&
    (item.order === undefined || (typeof item.order === "number" && Number.isInteger(item.order) && item.order >= 0));
}

/** Normalize legacy attachments at the host seam without changing the persisted desktop data. */
export function normalizeManagerImageAttachments(attachments: readonly ManagerImageAttachment[] | undefined): ManagerImageAttachment[] {
  return (attachments ?? []).map((attachment, index) => ({
    ...attachment,
    attachmentId: attachment.attachmentId?.trim() || `legacy-${index}`,
    displayName: attachment.displayName?.trim() || attachment.path.split(/[\\/]/).pop() || `image-${index + 1}`,
    order: attachment.order ?? index,
  })).sort((left, right) => (left.order ?? 0) - (right.order ?? 0));
}

export function isMainAgentConfig(value: unknown): value is MainAgentConfig {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  if (item.provider === "antigravity") {
    return (item.transport === undefined || item.transport === "sdk" || item.transport === "cli" || item.transport === "cli_interactive") &&
      (item.sdkSessionRef === undefined || typeof item.sdkSessionRef === "string") &&
      (item.legacyCliConversationId === undefined || typeof item.legacyCliConversationId === "string");
  }
  return item.provider === "openai_compatible" && typeof item.baseUrl === "string" && typeof item.modelId === "string";
}

export function isCodingAgentConfig(value: unknown): value is CodingAgentConfig {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  if (item.provider === "codex") return true;
  return item.provider === "pi" && typeof item.baseUrl === "string" && typeof item.modelId === "string";
}

export function validateManagerAction(value: unknown): ManagerAction | undefined {
  if (!value || typeof value !== "object") return undefined;
  const item = value as Record<string, unknown>;
  if (item.schemaVersion !== 1 || !["delegate_code", "respond", "ask_user"].includes(String(item.action)) || typeof item.message !== "string") return undefined;
  return { schemaVersion: 1, action: item.action as ManagerAction["action"], message: item.message };
}

export function validateCodeResult(value: unknown): CodeResult | undefined {
  if (!value || typeof value !== "object") return undefined;
  const item = value as any;
  if (!["completed", "blocked", "failed", "cancelled"].includes(String(item.status)) || typeof item.summaryForUser !== "string" || typeof item.needsUserDecision !== "boolean") return undefined;
  if (!Array.isArray(item.technicalDecisions) || !item.technicalDecisions.every((entry: any) => entry && typeof entry.decision === "string" && typeof entry.reason === "string")) return undefined;
  if (!Array.isArray(item.filesChanged) || !item.filesChanged.every((entry: any) => entry && typeof entry.path === "string" && Number.isInteger(entry.additions) && entry.additions >= 0 && Number.isInteger(entry.deletions) && entry.deletions >= 0)) return undefined;
  if (!Array.isArray(item.validation) || !item.validation.every((entry: any) => entry && typeof entry.command === "string" && ["passed", "failed", "skipped"].includes(String(entry.status)) && typeof entry.summary === "string")) return undefined;
  if (!Array.isArray(item.questions) || !item.questions.every((entry: unknown) => typeof entry === "string")) return undefined;
  return item as CodeResult;
}

export function normalizeStartConfig(message: Extract<ManagerClientMessage, { type: "start_manager_session" }>): { mainAgent: MainAgentConfig; codingAgent: CodingAgentConfig } {
  if (message.mainAgent && message.codingAgent) {
    const mainAgent = message.mainAgent.provider === "antigravity"
      ? { ...message.mainAgent, transport: message.mainAgent.transport ?? "cli", model: normalizeAntigravityModelId(message.mainAgent.model) }
      : message.mainAgent;
    return { mainAgent, codingAgent: message.codingAgent };
  }
  return {
    mainAgent: { provider: "antigravity", transport: "cli", executable: message.agyPath, conversationId: message.agyConversationId, legacyCliConversationId: message.agyConversationId, model: normalizeAntigravityModelId(message.agyModel), effort: message.effort, timeoutSeconds: message.timeoutSeconds },
    codingAgent: { provider: "pi", baseUrl: message.baseUrl ?? "", modelId: message.modelId ?? "", apiKey: message.apiKey, sessionFile: message.piSessionFile, autoApproveSafeCommands: message.autoApproveSafeCommands, autoApproveGitOperations: message.autoApproveGitOperations },
  };
}

/** `agy models` prints a stable slug followed by a human-readable label.
 * Persisting that whole display line (including its tab separator) makes AGY
 * treat the model selection as invalid, so use the slug field consistently. */
export function normalizeAntigravityModelId(value: string | undefined): string | undefined {
  // The CLI may mark the selected model with a leading `*`.  Strip that
  // marker before taking the first token; otherwise a line such as
  // `* gemini-...  Display Name` would normalize to an empty id.
  const text = (value ?? "").trim().replace(/\\t/g, " ").replace(/^(?:[*•]\s*)+/, "");
  if (!text) return undefined;
  const token = text.split(/\s+/)[0].trim();
  return token || undefined;
}
