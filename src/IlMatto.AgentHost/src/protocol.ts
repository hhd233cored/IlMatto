export type ClientMessage =
  | { type: "start_session"; sessionId: string; workspacePath: string; baseUrl: string; modelId: string; apiKey?: string; autoApproveSafeCommands?: boolean; autoApproveGitOperations?: boolean; sessionFile?: string; restoreTranscript?: RestoreTranscriptMessage[]; mode?: AgentSessionMode }
  | { type: "send_message"; sessionId: string; text: string }
  | { type: "code_task"; sessionId: string; taskId: string; userRequest: string }
  | { type: "approve_tool_call"; sessionId: string; callId: string; approved: boolean }
  | { type: "cancel"; sessionId: string }
  | { type: "new_session"; sessionId: string; workspacePath: string; baseUrl: string; modelId: string; apiKey?: string; autoApproveSafeCommands?: boolean; autoApproveGitOperations?: boolean; sessionFile?: string; restoreTranscript?: RestoreTranscriptMessage[]; mode?: AgentSessionMode }
  | { type: "get_commands"; sessionId: string }
  | { type: "get_git_overview"; sessionId: string }
  | { type: "get_git_diff"; sessionId: string; scope: "working" | "staged"; path?: string }
  | { type: "delete_session"; sessionId: string; sessionFile?: string }
  | { type: "shutdown"; sessionId?: string };

export type RestoreTranscriptMessage = { role: "user" | "assistant"; text: string };
export type AgentSessionMode = "interactive" | "coding_worker";

export type CodeResult = {
  status: "completed" | "blocked" | "failed" | "cancelled";
  summaryForUser: string;
  technicalDecisions: Array<{ decision: string; reason: string }>;
  filesChanged: Array<{ path: string; additions: number; deletions: number }>;
  validation: Array<{ command: string; status: "passed" | "failed" | "skipped"; summary: string }>;
  questions: string[];
  needsUserDecision: boolean;
};

export type SlashCommandInfo = { name: string; description?: string; source: string };
export type GitFileChange = { path: string; status: string; kind: "staged" | "unstaged" | "untracked" };
export type GitBranch = { name: string; isCurrent: boolean; upstream?: string };
export type GitCommit = { id: string; shortId: string; subject: string; author: string; date: string };
export type GitRemote = { name: string; fetchUrl?: string; pushUrl?: string };
export type GitOverview = { isRepository: boolean; message?: string; root?: string; branch?: string; upstream?: string; ahead: number; behind: number; staged: GitFileChange[]; unstaged: GitFileChange[]; untracked: GitFileChange[]; branches: GitBranch[]; commits: GitCommit[]; remotes: GitRemote[] };

export type HostMessage =
  | { type: "host_ready"; version: string }
  | { type: "session_ready"; sessionId: string; sessionFile?: string; restored: boolean; legacyRestored: boolean; workspacePath: string }
  | { type: "session_deleted"; sessionId: string; sessionFile?: string }
  | { type: "slash_commands"; sessionId: string; commands: SlashCommandInfo[] }
  | { type: "assistant_delta"; sessionId: string; text: string }
  | { type: "thinking_delta"; sessionId: string; text: string }
  | { type: "assistant_completed"; sessionId: string; text: string }
  | { type: "code_result"; sessionId: string; taskId: string; result: CodeResult }
  | { type: "tool_approval_request"; sessionId: string; callId: string; tool: string; summary: string; details: string; diff?: string }
  | { type: "tool_started"; sessionId: string; callId: string; tool: string; command?: string }
  | { type: "tool_output"; sessionId: string; callId: string; tool: string; text: string }
  | { type: "tool_completed"; sessionId: string; callId: string; tool: string; ok: boolean; summary: string; output?: string; diff?: string; autoApproved?: boolean }
  | { type: "git_overview"; sessionId: string; overview: GitOverview }
  | { type: "git_diff"; sessionId: string; scope: "working" | "staged"; path?: string; content: string; truncated: boolean }
  | { type: "command_result"; sessionId: string; command: string; message: string }
  | { type: "session_state"; sessionId: string; state: "idle" | "thinking" | "waiting_approval" | "compacting" | "cancelled" | "error" }
  | { type: "context_compaction_start"; sessionId: string; reason: "manual" | "threshold" | "overflow"; tokensBefore?: number }
  | { type: "context_compaction_end"; sessionId: string; reason: "manual" | "threshold" | "overflow"; summary?: string; tokensBefore?: number; aborted: boolean; willRetry: boolean; errorMessage?: string }
  | { type: "session_metrics"; sessionId: string; contextTokens?: number; contextWindow?: number; contextPercent?: number; cacheReadTokens?: number; cacheWriteTokens?: number; cacheStatsAvailable: boolean }
  | { type: "error"; sessionId?: string; message: string; code?: string };

export function isClientMessage(value: unknown): value is ClientMessage {
  if (!value || typeof value !== "object") return false;
  const type = (value as { type?: unknown }).type;
  const message = value as Record<string, unknown>;
  const hasSessionId = typeof message.sessionId === "string" && message.sessionId.length > 0;
  const hasStartFields = hasSessionId && typeof message.workspacePath === "string" && typeof message.baseUrl === "string" && typeof message.modelId === "string";
  const modeValid = message.mode === undefined || message.mode === "interactive" || message.mode === "coding_worker";
  const transcriptValid = message.restoreTranscript === undefined || (Array.isArray(message.restoreTranscript) && message.restoreTranscript.every((item) => Boolean(item) && typeof item === "object" && ((item as any).role === "user" || (item as any).role === "assistant") && typeof (item as any).text === "string"));
  switch (type) {
    case "start_session":
    case "new_session": return hasStartFields && modeValid && (message.sessionFile === undefined || typeof message.sessionFile === "string") && transcriptValid;
    case "send_message": return hasSessionId && typeof message.text === "string";
    case "code_task": return hasSessionId && typeof message.taskId === "string" && message.taskId.length > 0 && typeof message.userRequest === "string" && message.userRequest.length > 0;
    case "approve_tool_call": return hasSessionId && typeof message.callId === "string" && typeof message.approved === "boolean";
    case "cancel":
    case "get_commands":
    case "get_git_overview": return hasSessionId;
    case "get_git_diff": return hasSessionId && (message.scope === "working" || message.scope === "staged") && (message.path === undefined || typeof message.path === "string");
    case "delete_session": return hasSessionId && (message.sessionFile === undefined || typeof message.sessionFile === "string");
    case "shutdown": return message.sessionId === undefined || typeof message.sessionId === "string";
    default: return false;
  }
}
