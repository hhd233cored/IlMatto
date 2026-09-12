import assert from "node:assert/strict";
import { test } from "node:test";
import { isManagerClientMessage, isUnifiedAntigravityConfig, normalizeAntigravityModelId, normalizeManagerImageAttachments, normalizeStartConfig, resolveAntigravityConversationId, validateCodeResult, validateManagerAction } from "./protocol.js";

test("manager protocol validates strict routing messages", () => {
  assert.equal(isManagerClientMessage({ type: "activate_manager_session", sessionId: "s" }), true);
  assert.equal(isManagerClientMessage({ type: "activate_manager_session", sessionId: "" }), false);
  assert.equal(isManagerClientMessage({ type: "send_manager_message", sessionId: "s", text: "hello" }), true);
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
  assert.equal(isManagerClientMessage({ type: "list_agent_models", sessionId: "s", provider: "unsupported" }), false);
  assert.equal(isManagerClientMessage({ type: "list_agent_models", sessionId: "s", provider: "pi" }), false);
  assert.equal(isManagerClientMessage({ type: "browser_request", sessionId: "s", requestId: "b1", operation: "browser_tabs" }), false);
  assert.equal(isManagerClientMessage({ type: "browser_request", sessionId: "s", requestId: "b1", operation: "tabs" }), true);
  assert.equal(isManagerClientMessage({ type: "browser_request", sessionId: "s", requestId: "b2", operation: "navigate", url: "https://example.test" }), true);
  assert.equal(isManagerClientMessage({ type: "browser_request", sessionId: "s", requestId: "b3", operation: "navigate", url: "file:///secret" }), false);
  assert.equal(isManagerClientMessage({ type: "browser_set_visibility", sessionId: "s", visible: true }), true);
  assert.equal(isManagerClientMessage({ type: "browser_approve_action", sessionId: "s", actionId: "a", approved: false }), true);
  assert.equal(isManagerClientMessage({
    type: "browser_permissions_update", sessionId: "s",
    browserPermissions: { navigate: true, click: true, upload: false, download: false, evaluate: false, coordinate: false },
  }), true);
  assert.equal(isManagerClientMessage({
    type: "browser_permissions_update", sessionId: "s", browserPermissions: { unknown: true },
  }), false);
  assert.equal(isManagerClientMessage({
    type: "browser_request", sessionId: "s", requestId: "b4", operation: "evaluate",
    expression: "document.title", permission: "evaluate", write: true,
  }), true);
  assert.equal(isManagerClientMessage({
    type: "browser_request", sessionId: "s", requestId: "b5", operation: "mouse",
    x: "10", y: 20,
  }), false);
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
    { provider: "antigravity", executable: "agy", executionPolicy: "approval" },
  ];
  for (const mainAgent of mainAgents) {
    for (const codingAgent of codingAgents) {
      assert.equal(isManagerClientMessage({ type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent, codingAgent }), true);
    }
  }
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: mainAgents[0], codingAgent: codingAgents[0],
    executorProfiles: { antigravity: codingAgents[1], pi: codingAgents[0] },
  }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: mainAgents[0], codingAgent: codingAgents[0],
    executorProfiles: { unsupported: codingAgents[0] },
  }), false);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: mainAgents[0], codingAgent: codingAgents[0],
    browserPermissions: { navigate: true, click: true, upload: true, evaluate: false, coordinate: false },
  }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: mainAgents[0], codingAgent: codingAgents[0],
    browserPermissions: { click: "yes" },
  }), false);
  assert.equal(isManagerClientMessage({ type: "start_manager_session", sessionId: "s", workspacePath: "C:\\repo", mainAgent: { provider: "unknown" }, codingAgent: codingAgents[0] }), false);
});

test("manager protocol accepts companion memory patches on their matching operations", () => {
  assert.equal(isManagerClientMessage({
    type: "companion_memory_request", sessionId: "memory-session", requestId: "summary-1", operation: "session_update",
    patch: { summaryPatch: "讨论了旧信件", keyEvents: ["讨论旧信件"] },
  }), true);
  assert.equal(isManagerClientMessage({
    type: "companion_memory_request", sessionId: "memory-session", requestId: "profile-1", operation: "profile_update",
    profilePatch: { section: "preferences", add: ["喜欢简洁回复"] },
  }), true);
  assert.equal(isManagerClientMessage({
    type: "companion_memory_request", sessionId: "memory-session", requestId: "bad-1", operation: "session_update",
    profilePatch: { section: "invalid", add: ["无效章节"] },
  }), false);
  assert.equal(isManagerClientMessage({
    type: "companion_memory_request", sessionId: "memory-session", requestId: "read-1", operation: "session_read_page",
    targetSessionId: "history-session", cursor: "opaque-cursor", limit: 20,
  }), true);
  assert.equal(isManagerClientMessage({
    type: "companion_memory_request", sessionId: "memory-session", requestId: "read-2", operation: "session_read_page", limit: 21,
  }), false);
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
    codingAgent: { provider: "antigravity", executable: "agy" },
    companionProfile: { characterPrompt: "角色", userProfile: "用户", relationshipSummary: "关系" },
    conversationHistory: [{ role: "user", text: "你好" }, { role: "assistant", text: "你好呀" }],
  }), true);
  assert.equal(isManagerClientMessage({
    type: "start_manager_session", sessionId: "sdk-session", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", transport: "invalid" },
    codingAgent: { provider: "antigravity" },
  }), false);
  const normalized = normalizeStartConfig({
    type: "start_manager_session", sessionId: "legacy", workspacePath: "C:\\repo",
    mainAgent: { provider: "antigravity", legacyCliConversationId: "legacy-conversation" },
    codingAgent: { provider: "antigravity" },
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
