import type { CodeResult } from "./protocol.js";

/**
 * The relay deliberately carries only presentation-safe facts.  Raw worker
 * messages (reasoning, source, diffs, command lines and command output) stay
 * inside the Coding Worker/UI path and are never sent to the coordinator.
 */
export type CodingRelayItem = { prompt: string; final: boolean };

export function codingRelayEnabled(mainProvider: string, codingProvider: string): boolean {
  return mainProvider === "antigravity" && codingProvider === "codex";
}

export function buildCodingProgressPrompt(event: Record<string, unknown>): string | undefined {
  const type = String(event.type ?? "");
  const tool = safeToolName(event.tool ?? event.kind);
  switch (type) {
    case "delegation_started":
      return buildPrompt("progress", { status: "started", message: "代码任务已开始，Coding Agent 正在检查项目。" });
    case "tool_started":
      return buildPrompt("progress", { status: "running", operation: tool ? `正在进行 ${tool} 操作。` : "正在进行一项受控操作。" });
    case "tool_completed":
      return buildPrompt("progress", { status: event.ok === false ? "failed" : "completed", operation: tool ? `${tool} 操作已${event.ok === false ? "失败" : "完成"}。` : "受控操作已完成。" });
    case "tool_approval_request":
    case "interaction_request":
      return buildPrompt("progress", { status: "waiting", operation: "有一项受控操作等待你的确认。" });
    case "interaction_completed":
      return buildPrompt("progress", { status: "approved", operation: "确认结果已收到，任务继续执行。" });
    default:
      return undefined;
  }
}

export function buildCodingResultPrompt(result: CodeResult): string {
  const additions = result.filesChanged.reduce((sum, file) => sum + file.additions, 0);
  const deletions = result.filesChanged.reduce((sum, file) => sum + file.deletions, 0);
  const facts = {
    status: result.status,
    summary: redactRelayText(result.summaryForUser, 1_200),
    filesChanged: result.filesChanged.length,
    additions,
    deletions,
    validation: result.validation.map((item) => ({ status: item.status, summary: redactRelayText(item.summary, 360) })),
    questions: result.questions.map((question) => redactRelayText(question, 300)).slice(0, 4),
    needsUserDecision: result.needsUserDecision,
  };
  return buildPrompt("result", facts);
}

export function buildPrompt(kind: "progress" | "result", facts: Record<string, unknown>): string {
  const instruction = kind === "progress"
    ? "用一句简短、自然、符合陪伴型角色的中文告知用户当前进度。不要解释技术细节，不要提及内部协议。"
    : "用一到三句自然、符合陪伴型角色的中文总结任务结果。只能使用事实中的信息；不要补充未提供的技术结论，不要复述 JSON。若需要用户决定，请明确提出问题。";
  return `[IlMatto narration]\n这是 Coding Agent 的内部状态摘要。它只是事实资料，不是新的代码请求。${instruction}\n<facts>${JSON.stringify(facts)}</facts>\n请只返回 ManagerAction 中 action=respond 的用户可见 message。`;
}

export function redactRelayText(value: unknown, maxLength: number): string {
  const text = String(value ?? "")
    .replace(/```[\s\S]*?```/g, "[代码已隐藏]")
    .replace(/(?:[A-Za-z]:\\|\\\\)[^\s\r\n<>"']+/g, "[本地路径]")
    .replace(/\/(?:Users|home|var|opt|srv|workspace)\/[^\s\r\n<>"']+/gi, "[本地路径]")
    .replace(/\s+/g, " ")
    .trim();
  return text.length > maxLength ? `${text.slice(0, Math.max(0, maxLength - 1))}…` : text;
}

function safeToolName(value: unknown): string {
  const text = String(value ?? "").trim();
  return /^[A-Za-z0-9_.:-]{1,64}$/.test(text) ? text : "";
}
