export type ManagerAction = {
  schemaVersion: 1;
  action: "delegate_code" | "respond" | "ask_user";
  message: string;
};

export type CompanionProfile = {
  characterPrompt: string;
  characterName?: string;
  /** Legacy fields are accepted from older desktop builds and ignored by the
   * active unified Manager. The active user profile lives in profile.md. */
  userProfile?: string;
  relationshipSummary?: string;
};

export type ProfileSection = "basic" | "interests" | "preferences" | "boundaries" | "current_topics";

export type SessionSummary = {
  version: 1;
  sessionId: string;
  title: string;
  summary: string;
  keyEvents: string[];
  openLoops: string[];
  keywords: string[];
  updatedAt: string;
};

export type TranscriptEntry = {
  role: "user" | "assistant";
  text: string;
  createdAt: string;
  turnId?: string;
};

export type SessionSummaryPatch = {
  title?: string;
  summaryPatch?: string;
  keyEvents?: string[];
  openLoops?: string[];
  keywords?: string[];
};

export type ProfilePatch = {
  section: ProfileSection;
  add?: string[];
  remove?: string[];
};

export type CompanionMemoryOperation = "session_search" | "session_open" | "session_update" | "profile_update";

export type CompanionMemoryRequest = {
  type: "companion_memory_request";
  sessionId: string;
  requestId: string;
  operation: CompanionMemoryOperation;
  query?: string;
  targetSessionId?: string;
  limit?: number;
  patch?: SessionSummaryPatch;
  profilePatch?: ProfilePatch;
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

export type CodingProvider = "antigravity" | "pi";
export type AntigravityExecutionPolicy = "approval" | "safe_tests" | "autonomous";
export type AntigravityToolPermission = "request-review" | "proceed-in-sandbox" | "always-proceed" | "strict";

export type AgentModelInfo = {
  id: string;
  displayName: string;
  efforts: string[];
};

/** A private, local-only audit item. It is never valid coordinator input. */
export type TaskTraceEvent = {
  taskId: string;
  provider: CodingProvider;
  kind: "reasoning_summary" | "assistant_text" | "tool_started" | "tool_output" | "tool_completed" | "approval" | "result" | "error";
  timestamp: string;
  text: string;
  details?: Record<string, unknown>;
};

/** The only payload another executor may receive from a completed code task. */
export type HandoffPacket = {
  taskId: string;
  provider: CodingProvider;
  status: CodeResult["status"];
  summaryForUser: string;
  filesChanged: CodeResult["filesChanged"];
  validation: CodeResult["validation"];
  workspaceSnapshotId?: string;
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
  | { provider: "antigravity"; transport?: AntigravityTransport; executable?: string; conversationId?: string; sdkSessionRef?: string; legacyCliConversationId?: string; model?: string; effort?: "low" | "medium" | "high"; timeoutSeconds?: number; toolPermission?: AntigravityToolPermission; terminalSandbox?: boolean }
  | { provider: "openai_compatible"; baseUrl: string; modelId: string; apiKey?: string; sessionFile?: string; timeoutSeconds?: number };

export type CodingAgentConfig =
  | { provider: "pi"; baseUrl: string; modelId: string; apiKey?: string; sessionFile?: string; autoApproveSafeCommands?: boolean; autoApproveGitOperations?: boolean }
  | { provider: "antigravity"; executable?: string; model?: string; effort?: "low" | "medium" | "high"; executionPolicy?: AntigravityExecutionPolicy; conversationId?: string };

export type ExecutorProfiles = Partial<Record<CodingProvider, CodingAgentConfig>>;

export type CodingInteractionKind = "command_approval" | "file_approval" | "permissions" | "question" | "mcp_form" | "mcp_url";

export type ManagerClientMessage =
  | {
      type: "start_manager_session"; sessionId: string; workspacePath: string;
      /** Unified v2 shape. `mainAgent`/`codingAgent` remain accepted for old desktop builds. */
      antigravity?: { provider?: "antigravity"; executable?: string; conversationId?: string; model?: string; effort?: "low" | "medium" | "high"; toolPermission?: AntigravityToolPermission; terminalSandbox?: boolean };
      mainAgent?: MainAgentConfig; codingAgent?: CodingAgentConfig; executorProfiles?: ExecutorProfiles; awaitingCodingDecision?: boolean;
      companionProfile?: CompanionProfile; conversationHistory?: CompanionHistoryItem[];
      agyPath?: string; agyConversationId?: string; piSessionFile?: string; awaitingPiDecision?: boolean;
      baseUrl?: string; modelId?: string; apiKey?: string; autoApproveSafeCommands?: boolean; autoApproveGitOperations?: boolean;
      agyModel?: string; effort?: "low" | "medium" | "high"; timeoutSeconds?: number;
  }
  | { type: "activate_manager_session"; sessionId: string }
  | { type: "send_manager_message"; sessionId: string; text: string; executor?: CodingProvider; attachments?: ManagerImageAttachment[]; generateTitle?: boolean }
  | { type: "approve_coding_tool"; sessionId: string; callId: string; approved: boolean }
  | CompanionMemoryRequest
  | { type: "cancel_manager_turn"; sessionId: string; target?: "antigravity" | "all" }
  | { type: "request_verification"; sessionId: string; taskId: string }
  | { type: "list_agent_models"; sessionId: string; provider: "antigravity" }
  | { type: "delete_manager_session"; sessionId: string; workspacePath?: string; mainAgent?: MainAgentConfig; codingAgent?: CodingAgentConfig; piSessionFile?: string; coordinatorSessionFile?: string }
  | { type: "shutdown"; sessionId?: string };

export type ManagerHostMessage =
  | { type: "manager_host_ready"; version: string }
  | { type: "manager_session_ready"; sessionId: string; mainProvider: MainAgentConfig["provider"]; codingProvider: CodingProvider; mainSessionRef?: string; codingSessionRef?: string; agyConversationId?: string; piSessionFile?: string; antigravityAvailable?: boolean; authenticated?: boolean; version?: string; antigravityTransport?: AntigravityTransport }
  | { type: "provider_status"; sessionId: string; layer: "main" | "coding"; provider: MainAgentConfig["provider"] | CodingProvider; available: boolean; authenticated?: boolean; version?: string; message?: string; policy?: string }
  | { type: "antigravity_status"; sessionId: string; available: boolean; authenticated: boolean; version?: string; message?: string; models?: AgentModelInfo[] }
  | { type: "manager_state"; sessionId: string; state: "idle" | "routing" | "responding" | "coding" | "waiting_approval" | "cancelled" | "error"; turnId?: string; taskId?: string; provider?: "antigravity" | "api_manager"; startedAt?: string; completedAt?: string; durationMs?: number }
  | { type: "manager_delta"; sessionId: string; source: "antigravity" | "api_manager"; text: string }
  | { type: "manager_thinking_delta"; sessionId: string; source: "antigravity" | "api_manager"; text: string }
  | { type: "manager_tool_status"; sessionId: string; source: "antigravity" | "api_manager"; callId: string; tool: string; text: string; state: "started" | "completed" }
  | { type: "manager_completed"; sessionId: string; source: "antigravity" | "api_manager"; text: string; action: "delegate_code" | "respond" | "ask_user"; final?: boolean; turnId?: string; startedAt?: string; completedAt?: string; durationMs?: number }
  | { type: "manager_title"; sessionId: string; title: string }
  | { type: "manager_metrics"; sessionId: string; provider: "antigravity" | "api_manager"; contextTokens?: number; contextWindow?: number; cacheReadTokens?: number; antigravityCacheReadTokens?: number }
  | { type: "delegation_started"; sessionId: string; taskId: string; provider: CodingProvider; startedAt?: string }
  | { type: "coding_delta"; sessionId: string; taskId?: string; source: CodingProvider; text: string }
  | { type: "coding_thinking_delta"; sessionId: string; taskId?: string; source: CodingProvider; text: string }
  | { type: "coding_tool_approval_request"; sessionId: string; callId: string; tool: string; summary: string; details: string; diff?: string }
  | { type: "companion_memory_response"; sessionId: string; requestId: string; ok: boolean; data?: unknown; error?: { code: string; message: string } }
  | { type: "coding_tool_started"; sessionId: string; callId: string; tool: string; command?: string; taskId?: string; source?: CodingProvider }
  | { type: "coding_tool_output"; sessionId: string; callId: string; tool: string; text: string; taskId?: string; source?: CodingProvider }
  | { type: "coding_tool_completed"; sessionId: string; callId: string; tool: string; ok: boolean; summary: string; command?: string; output?: string; diff?: string; autoApproved?: boolean; taskId?: string; source?: CodingProvider }
  | { type: "coding_completed"; sessionId: string; taskId: string; source: CodingProvider; status: "completed" | "failed" | "cancelled" | "partial"; text: string; startedAt?: string; completedAt?: string; durationMs?: number }
  | { type: "code_result"; sessionId: string; taskId: string; result: CodeResult }
  | { type: "verification_started"; sessionId: string; taskId: string; provider: "antigravity" }
  | { type: "verification_result"; sessionId: string; taskId: string; result: CodeResult }
  | { type: "task_trace"; sessionId: string; event: TaskTraceEvent }
  | { type: "agent_models"; sessionId: string; provider: "antigravity"; available: boolean; authenticated: boolean; models: AgentModelInfo[]; message?: string }
  | { type: "manager_session_deleted"; sessionId: string }
  | { type: "manager_error"; sessionId?: string; provider?: "antigravity" | "api_manager"; code: string; message: string };

export function isManagerClientMessage(value: unknown): value is ManagerClientMessage {
  if (!value || typeof value !== "object") return false;
  const message = value as Record<string, unknown>;
  const session = typeof message.sessionId === "string" && message.sessionId.length > 0;
  switch (message.type) {
    case "start_manager_session":
      if (!session || typeof message.workspacePath !== "string") return false;
      if (message.mainAgent !== undefined && !isMainAgentConfig(message.mainAgent)) return false;
      if (message.codingAgent !== undefined && !isCodingAgentConfig(message.codingAgent)) return false;
      if (message.antigravity !== undefined && !isUnifiedAntigravityConfig(message.antigravity)) return false;
      if (message.executorProfiles !== undefined && !isExecutorProfiles(message.executorProfiles)) return false;
      if (message.companionProfile !== undefined && !isCompanionProfile(message.companionProfile)) return false;
      if (message.conversationHistory !== undefined &&
          (!Array.isArray(message.conversationHistory) || !message.conversationHistory.every(isCompanionHistoryItem))) return false;
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

export function isCompanionProfile(value: unknown): value is CompanionProfile {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  return typeof item.characterPrompt === "string" &&
    (item.characterName === undefined || typeof item.characterName === "string") &&
    (item.userProfile === undefined || typeof item.userProfile === "string") &&
    (item.relationshipSummary === undefined || typeof item.relationshipSummary === "string");
}

function isCompanionMemoryRequest(value: Record<string, unknown>, session: boolean): value is CompanionMemoryRequest {
  if (!session || typeof value.requestId !== "string" || !value.requestId ||
      !["session_search", "session_open", "session_update", "profile_update"].includes(String(value.operation))) return false;
  if (value.query !== undefined && typeof value.query !== "string") return false;
  if (value.targetSessionId !== undefined && typeof value.targetSessionId !== "string") return false;
  if (value.limit !== undefined && (typeof value.limit !== "number" || !Number.isInteger(value.limit) || value.limit < 1 || value.limit > 5)) return false;
  if (value.patch !== undefined && !isSessionSummaryPatch(value.patch)) return false;
  if (value.profilePatch !== undefined && !isProfilePatch(value.profilePatch)) return false;
  return true;
}

function isSessionSummaryPatch(value: unknown): value is SessionSummaryPatch {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const item = value as Record<string, unknown>;
  return (item.title === undefined || typeof item.title === "string") &&
    (item.summaryPatch === undefined || typeof item.summaryPatch === "string") &&
    isOptionalStringArray(item.keyEvents) && isOptionalStringArray(item.openLoops) && isOptionalStringArray(item.keywords);
}

function isProfilePatch(value: unknown): value is ProfilePatch {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const item = value as Record<string, unknown>;
  return isProfileSection(item.section) && isOptionalStringArray(item.add) && isOptionalStringArray(item.remove);
}

function isProfileSection(value: unknown): value is ProfileSection {
  return value === "basic" || value === "interests" || value === "preferences" || value === "boundaries" || value === "current_topics";
}

function isOptionalStringArray(value: unknown): value is string[] | undefined {
  return value === undefined || (Array.isArray(value) && value.every((item) => typeof item === "string"));
}

/** Validate the compact single-session start payload without requiring any of
 * the legacy mainAgent/codingAgent fields.  Keeping this check at the pipe
 * boundary prevents malformed optional values from reaching the CLI spawn
 * path while preserving backwards compatibility for old desktop payloads. */
export function isUnifiedAntigravityConfig(value: unknown): value is NonNullable<Extract<ManagerClientMessage, { type: "start_manager_session" }>["antigravity"]> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const item = value as Record<string, unknown>;
  return (item.provider === undefined || item.provider === "antigravity") &&
    (item.executable === undefined || typeof item.executable === "string") &&
    (item.conversationId === undefined || typeof item.conversationId === "string") &&
    (item.model === undefined || typeof item.model === "string") &&
    (item.effort === undefined || item.effort === "low" || item.effort === "medium" || item.effort === "high") &&
    (item.toolPermission === undefined || isAntigravityToolPermission(item.toolPermission)) &&
    (item.terminalSandbox === undefined || typeof item.terminalSandbox === "boolean");
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

export function isCodingAgentConfig(value: unknown): value is CodingAgentConfig {
  if (!value || typeof value !== "object") return false;
  const item = value as Record<string, unknown>;
  if (item.provider === "antigravity") return item.executionPolicy === undefined || ["approval", "safe_tests", "autonomous"].includes(String(item.executionPolicy));
  return item.provider === "pi" && typeof item.baseUrl === "string" && typeof item.modelId === "string";
}

export function isAntigravityToolPermission(value: unknown): value is AntigravityToolPermission {
  return value === "request-review" || value === "proceed-in-sandbox" || value === "always-proceed" || value === "strict";
}

export function isCodingProvider(value: unknown): value is CodingProvider {
  return value === "antigravity" || value === "pi";
}

/** Select the reusable CLI conversation id from both the current and legacy
 * start-message shapes. SDK-only references are intentionally ignored. */
export function resolveAntigravityConversationId(message: Extract<ManagerClientMessage, { type: "start_manager_session" }>): string | undefined {
  if (message.mainAgent?.provider === "antigravity") {
    if (message.mainAgent.transport === "sdk") return firstNonEmpty(message.mainAgent.legacyCliConversationId);
    return firstNonEmpty(message.mainAgent.conversationId, message.mainAgent.legacyCliConversationId);
  }
  return firstNonEmpty(message.antigravity?.conversationId, message.agyConversationId);
}

function firstNonEmpty(...values: Array<string | undefined>): string | undefined {
  return values.map((value) => value?.trim()).find((value): value is string => Boolean(value));
}

export function isExecutorProfiles(value: unknown): value is ExecutorProfiles {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  return Object.entries(value as Record<string, unknown>).every(([provider, config]) =>
    isCodingProvider(provider) && isCodingAgentConfig(config) && (config as CodingAgentConfig).provider === provider,
  );
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

export function normalizeStartConfig(message: Extract<ManagerClientMessage, { type: "start_manager_session" }>): { mainAgent: MainAgentConfig; codingAgent: CodingAgentConfig; executorProfiles: ExecutorProfiles } {
  if (message.antigravity) {
    const antigravity = {
      provider: "antigravity" as const,
      transport: "cli" as const,
      executable: message.antigravity.executable ?? message.agyPath,
      conversationId: message.antigravity.conversationId,
      legacyCliConversationId: undefined,
      model: normalizeAntigravityModelId(message.antigravity.model ?? message.agyModel),
      effort: message.antigravity.effort ?? message.effort,
      timeoutSeconds: 0,
      toolPermission: message.antigravity.toolPermission,
      terminalSandbox: message.antigravity.terminalSandbox,
    };
    const codingAgent: CodingAgentConfig = {
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
export function normalizeAntigravityModelId(value: string | undefined): string | undefined {
  // The CLI may mark the selected model with a leading `*`.  Strip that
  // marker before taking the first token; otherwise a line such as
  // `* gemini-...  Display Name` would normalize to an empty id.
  const text = (value ?? "").trim().replace(/\\t/g, " ").replace(/^(?:[*•]\s*)+/, "");
  if (!text) return undefined;
  const token = text.split(/\s+/)[0].trim();
  return token || undefined;
}
