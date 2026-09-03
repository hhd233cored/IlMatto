import type { CodeResult, CodingProvider, HandoffPacket, TaskTraceEvent } from "./protocol.js";

export type TaskExecutor = CodingProvider;
export type TaskPhase = "queued" | "implementing" | "awaiting_approval" | "implemented" | "verifying" | "completed" | "blocked" | "failed" | "cancelled";

export type ParsedExecutorDirective = {
  executor?: TaskExecutor;
  request: string;
};

/**
 * Task routing is deliberately deterministic.  A directive must be the first
 * standalone token; prose such as “could Codex help?” is ordinary text and
 * never changes the executing provider silently.
 */
export function parseExecutorDirective(input: string): ParsedExecutorDirective {
  const match = /^\s*@(?<provider>antigravity|pi|codex)(?=\s|$)\s*/i.exec(input);
  if (!match?.groups?.provider) return { request: input };
  return { executor: match.groups.provider.toLowerCase() as TaskExecutor, request: input.slice(match[0].length) };
}

export function defaultExecutorFor(mainProvider: "antigravity" | "openai_compatible"): TaskExecutor {
  return mainProvider === "antigravity" ? "antigravity" : "pi";
}

/** One workspace has one mutating/verification lease at a time. */
export class ExecutionLease {
  private holder?: { taskId: string; phase: TaskPhase };

  acquire(taskId: string, phase: Extract<TaskPhase, "implementing" | "verifying">): void {
    if (this.holder && this.holder.taskId !== taskId)
      throw new TaskOrchestratorError("WORKSPACE_BUSY", `工作区正由任务 ${this.holder.taskId} ${this.holder.phase === "verifying" ? "验证" : "处理"}。`);
    this.holder = { taskId, phase };
  }

  transition(taskId: string, phase: TaskPhase): void {
    if (!this.holder || this.holder.taskId !== taskId)
      throw new TaskOrchestratorError("LEASE_NOT_HELD", "任务没有持有工作区执行锁。");
    this.holder.phase = phase;
  }

  release(taskId: string): void {
    if (this.holder?.taskId === taskId) this.holder = undefined;
  }

  get active(): Readonly<{ taskId: string; phase: TaskPhase }> | undefined { return this.holder; }
}

export function buildHandoffPacket(taskId: string, provider: TaskExecutor, result: CodeResult, workspaceSnapshotId?: string): HandoffPacket {
  return {
    taskId,
    provider,
    status: result.status,
    summaryForUser: result.summaryForUser,
    filesChanged: result.filesChanged,
    validation: result.validation,
    workspaceSnapshotId,
  };
}

/** Keep private execution traces bounded before they reach local persistence. */
export function privateTraceEvent(
  taskId: string,
  provider: TaskExecutor,
  kind: TaskTraceEvent["kind"],
  text?: string,
  details?: Record<string, unknown>,
): TaskTraceEvent {
  return {
    taskId,
    provider,
    kind,
    timestamp: new Date().toISOString(),
    text: redactAndLimit(text ?? ""),
    details: details ? redactDetails(details) : undefined,
  };
}

export function redactAndLimit(value: string, maximum = 64_000): string {
  const redacted = value
    .replace(/(sk-[A-Za-z0-9_-]{8,}|AIza[\w-]{20,}|Bearer\s+[A-Za-z0-9._~+\/-]{12,})/gi, "[已脱敏]")
    .replace(/((?:api|access|auth|refresh)[_-]?token|password|secret)\s*[=:]\s*[^\s"']+/gi, "$1=[已脱敏]");
  return redacted.length <= maximum ? redacted : `${redacted.slice(0, maximum)}\n…[输出已截断]`;
}

function redactDetails(details: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(details).map(([key, value]) => [
    key,
    typeof value === "string" ? redactAndLimit(value, 16_000) : value,
  ]));
}

export class TaskOrchestratorError extends Error {
  constructor(readonly code: "WORKSPACE_BUSY" | "LEASE_NOT_HELD" | "VERIFICATION_NOT_ALLOWED", message: string) {
    super(message);
    this.name = "TaskOrchestratorError";
  }
}
