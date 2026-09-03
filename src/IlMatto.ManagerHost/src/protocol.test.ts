import assert from "node:assert/strict";
import { test } from "node:test";
import { isManagerClientMessage, isUnifiedAntigravityConfig, normalizeAntigravityModelId, normalizeManagerImageAttachments, normalizeStartConfig, parseCodexDirective, resolveAntigravityConversationId, validateCodeResult, validateManagerAction } from "./protocol.js";

test("manager protocol validates strict routing messages", () => {
  assert.equal(isManagerClientMessage({ type: "send_manager_message", sessionId: "s", text: "hello" }), true);
  assert.equal(isManagerClientMessage({ type: "send_manager_message", sessionId: "s", text: "@codex 修复测试", draftId: "draft-1" }), true);
  assert.equal(isManagerClientMessage({ type: "send_manager_message", sessionId: "s", text: "@codex 修复测试", draftId: "  " }), false);
  assert.equal(isManagerClientMessage({
    type: "send_manager_message", sessionId: "s", text: "根据截图修复布局",
    attachments: [{ type: "image", path: "C:\\tmp\\screen.png", displayName: "screen.png", mimeType: "image/png" }],
  }), true);
  assert.equal(isManagerClientMessage({
    type: "send_manager_message", sessionId: "s", text: "看图",
    attachments: [{ type: "file", path: "C:\\tmp\\screen.png" }],
  }), false);
  assert.equal(isManagerClientMessage({ type: "approve_coding_tool", sessionId: "s", callId: "c", approved: false }), true);
  assert.equal(isManagerClientMessage({ type: "send_manager_message", sessionId: "s", text: "" }), false);
  assert.equal(isManagerClientMessage({ type: "send_manager_message", sessionId: "s", text: "", attachments: [{ type: "image", path: "C:\\tmp\\screen.png" }] }), true);
  assert.deepEqual(validateManagerAction({ schemaVersion: 1, action: "delegate_code", message: "ignored" }), { schemaVersion: 1, action: "delegate_code", message: "ignored" });
  assert.equal(validateManagerAction({ schemaVersion: 1, action: "write_file", message: "" }), undefined);
  assert.equal(validateManagerAction({ schemaVersion: 2, action: "respond", message: "hello" }), undefined);
  assert.equal(isManagerClientMessage({ type: "list_agent_models", sessionId: "s", provider: "antigravity" }), true);
  assert.equal(isManagerClientMessage({ type: "list_agent_models", sessionId: "s", provider: "codex" }), true);
  assert.equal(isManagerClientMessage({ type: "list_agent_models", sessionId: "s", provider: "pi" }), false);
});

test("@codex directives only route a non-empty leading prompt", () => {
  assert.equal(parseCodexDirective("@codex 修复编译"), "修复编译");
  assert.equal(parseCodexDirective("  @CODEX: 运行测试\n并修复失败项  "), "运行测试\n并修复失败项");
  assert.equal(parseCodexDirective("请在说明中提到 @codex，但不要路由"), undefined);
  assert.equal(parseCodexDirective("@codex"), undefined);
  assert.equal(parseCodexDirective("@codex:   "), undefined);
});

test("managed image attachments preserve order and backfill legacy ids", () => {
  const attachments = normalizeManagerImageAttachments([
    { type: "image", path: "C:\\tmp\\second.png", order: 2 },
    { type: "image", path: "C:\\tmp\\first.png", order: 0, attachmentId: "first" },
  ]);
  assert.deepEqual(attachments.map((item) => item.order), [0, 2]);
  assert.equal(attachments[0].attachmentId, "first");
  assert.equal(attachments[1].attachmentId, "legacy-0");
});

test("manager protocol accepts all provider combinations and generic interactions", () => {
  const mainAgents = [
    { provider: "antigravity", executable: "agy" },
    { provider: "openai_compatible", baseUrl: "https://example.test/v1", modelId: "model" },
  ];
  const codingAgents = [
    { provider: "pi", baseUrl: "https://example.test/v1", modelId: "model" },
    { provider: "codex", executable: "codex", threadId: "thread-1" },
    { provider: "antigravity", executable: "agy", executionPolicy: "approval" },
  ];
  for (const mainAgent of mainAgents) {
    for (const codingAgent of codingAgents) {
      assert.equal(isManagerClientMessage({ type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent, codingAgent }), true);
    }
  }
  assert.equal(isManagerClientMessage({ type: "resolve_coding_interaction", sessionId: "s", requestId: "r", approved: true, values: { answer: "ok" } }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: mainAgents[0], codingAgent: codingAgents[0],
    executorProfiles: { antigravity: codingAgents[2], pi: codingAgents[0], codex: codingAgents[1] },
  }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: mainAgents[0], codingAgent: codingAgents[0],
    executorProfiles: { codex: codingAgents[0] },
  }), false);
  assert.equal(isManagerClientMessage({ type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: { provider: "unknown" }, codingAgent: codingAgents[0] }), false);
});

test("Codex approval policy accepts native values and the always extension", () => {
  for (const approvalPolicy of ["always", "untrusted", "on-request", "never"]) {
    assert.equal(isManagerClientMessage({
      type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
      mainAgent: { provider: "antigravity" }, codingAgent: { provider: "codex", approvalPolicy },
    }), true);
  }
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity" }, codingAgent: { provider: "codex", approvalPolicy: "granular" },
  }), false);
  for (const sandboxMode of ["read-only", "workspace-write", "danger-full-access"]) {
    assert.equal(isManagerClientMessage({
      type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
      mainAgent: { provider: "antigravity" }, codingAgent: { provider: "codex", sandboxMode },
    }), true);
  }
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity" }, codingAgent: { provider: "codex", sandboxMode: "full-access" },
  }), false);
});

test("manager protocol validates read-only Codex observation requests", () => {
  assert.equal(isManagerClientMessage({ type: "codex_observation_request", sessionId: "s", requestId: "r", operation: "draft_codex_task", workspacePath: "C:\\repo", prompt: "修复测试" }), true);
  assert.equal(isManagerClientMessage({ type: "codex_observation_request", sessionId: "s", requestId: "r", operation: "get_codex_status" }), true);
  assert.equal(isManagerClientMessage({ type: "codex_observation_request", sessionId: "s", requestId: "r", operation: "get_codex_report", taskId: "task-1" }), true);
  assert.equal(isManagerClientMessage({ type: "codex_observation_request", sessionId: "s", requestId: "r", operation: "submit_codex_task", prompt: "不应存在" }), false);
  assert.equal(isManagerClientMessage({ type: "codex_observation_request", sessionId: "s", requestId: "r", operation: "get_codex_diff", taskId: "task-1", maxBytes: 2_000_001 }), false);
});

test("unified Antigravity start messages use the compact validated shape", () => {
  assert.equal(isUnifiedAntigravityConfig({ provider: "antigravity", executable: "agy", conversationId: "conv-1", model: "gemini-test", effort: "high" }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "unified", workspacePath: "C:\\repo",
    antigravity: { executable: "agy", model: "gemini-test", effort: "medium" },
  }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "unified", workspacePath: "C:\\repo",
    antigravity: { effort: "xhigh" },
  }), false);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "unified", workspacePath: "C:\\repo",
    antigravity: { executable: 42 },
  }), false);
  const normalized = normalizeStartConfig({
    type: "start_manager_session", sessionId: "unified", workspacePath: "C:\\repo",
    antigravity: { executable: "agy", conversationId: "conv-1", model: "gemini-test-high", effort: "high" },
  });
  assert.equal(normalized.mainAgent.provider, "antigravity");
  if (normalized.mainAgent.provider === "antigravity") {
    assert.equal(normalized.mainAgent.executable, "agy");
    assert.equal(normalized.mainAgent.conversationId, "conv-1");
    assert.equal(normalized.mainAgent.model, "gemini-test-high");
  }
  assert.equal(normalized.codingAgent.provider, "antigravity");
});

test("SDK manager sessions validate transport, history, and preserve legacy identifiers", () => {
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "sdk-session", workspacePath: "C:\\repo",
    mainAgent: {
      provider: "antigravity", transport: "sdk", sdkSessionRef: "sdk-ref-abcdefghijklmnopqrstuvwxyz123456",
      legacyCliConversationId: "legacy-ref", model: "gemini-test", effort: "medium",
    },
    codingAgent: { provider: "codex", executable: "codex" },
    companionProfile: { characterPrompt: "角色", userProfile: "用户", relationshipSummary: "关系" },
    conversationHistory: [{ role: "user", text: "你好" }, { role: "assistant", text: "你好呀" }],
  }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "sdk-session", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", transport: "invalid" },
    codingAgent: { provider: "codex" },
  }), false);
  const normalized = normalizeStartConfig({
    type: "start_manager_session", sessionId: "legacy", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", legacyCliConversationId: "legacy-conversation" },
    codingAgent: { provider: "codex" },
  });
  assert.equal(normalized.mainAgent.provider, "antigravity");
  if (normalized.mainAgent.provider === "antigravity") {
    assert.equal(normalized.mainAgent.transport, "cli");
    assert.equal(normalized.mainAgent.legacyCliConversationId, "legacy-conversation");
  }
});

test("AGY resume id prefers mainAgent conversationId and falls back to legacy identifiers", () => {
  assert.equal(resolveAntigravityConversationId({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", conversationId: "current-id", legacyCliConversationId: "legacy-id" },
    codingAgent: { provider: "antigravity" },
  }), "current-id");
  assert.equal(resolveAntigravityConversationId({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", transport: "sdk", conversationId: "sdk-id", legacyCliConversationId: "legacy-id" },
    codingAgent: { provider: "antigravity" },
  }), "legacy-id");
  assert.equal(resolveAntigravityConversationId({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", conversationId: "  ", legacyCliConversationId: "legacy-id" },
    codingAgent: { provider: "antigravity" },
  }), "legacy-id");
  assert.equal(resolveAntigravityConversationId({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
    antigravity: { conversationId: "unified-id" },
  }), "unified-id");
});

test("legacy start messages migrate to Antigravity and Pi bindings", () => {
  const normalized = normalizeStartConfig({
    type: "start_manager_session", sessionId: "legacy", workspacePath: "C:\\repo",
    agyPath: "agy.exe", baseUrl: "https://example.test/v1", modelId: "model", apiKey: "secret",
  });
  assert.equal(normalized.mainAgent.provider, "antigravity");
  assert.equal(normalized.codingAgent.provider, "pi");
});

test("Antigravity model normalization keeps only the CLI slug", () => {
  assert.equal(normalizeAntigravityModelId("gemini-3.7-flash-high\tGemini 3.7 Flash (High)"), "gemini-3.7-flash-high");
  assert.equal(normalizeAntigravityModelId("* gemini-3.7-flash-medium  Gemini 3.7 Flash (Medium)"), "gemini-3.7-flash-medium");
  const normalized = normalizeStartConfig({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", model: "gemini-3.7-flash-high\tGemini 3.7 Flash (High)", effort: "high" },
    codingAgent: { provider: "pi", baseUrl: "https://example.test/v1", modelId: "model" },
  });
  assert.equal(normalized.mainAgent.provider, "antigravity");
  assert.equal(normalized.mainAgent.model, "gemini-3.7-flash-high");
});

test("CodeResult validation rejects ordinary assistant text", () => {
  assert.equal(validateCodeResult("done"), undefined);
  assert.ok(validateCodeResult({
    status: "completed", summaryForUser: "完成", technicalDecisions: [], filesChanged: [], validation: [], questions: [], needsUserDecision: false,
  }));
  assert.equal(validateCodeResult({ status: "completed", summaryForUser: "完成" }), undefined);
  assert.equal(validateCodeResult({
    status: "completed", summaryForUser: "完成", technicalDecisions: [], filesChanged: [{ path: "a", additions: -1, deletions: 0 }], validation: [], questions: [], needsUserDecision: false,
  }), undefined);
});
